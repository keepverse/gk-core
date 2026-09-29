using System.Text.Json;
using FusionRpg.Core.Delve.Loot;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Power;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `RpgStore.MintGrant`/`.MintGrantUnlocked` (party-dungeon-todo.md D4.12/D3.11, `mintAt`'s real,
/// callable Data-layer composition, 2026-09-07) — the real, DB-backed proof that closes the shared
/// blocker: a Unique-kind grant round-trips through a REAL persisted `ContainerRow`; an Equipment-kind
/// grant round-trips through the real atom catalog and a caller-supplied role-family table;
/// `MintGrantUnlocked` composes into an externally-owned transaction exactly like
/// `QuestRewardBankingComposabilityTests` already proves for `SaveInstanceUnlocked`/
/// `AcquireItemUnlocked`/`PersistLootUnlocked`; `MintGrant` stops at `SaveInstanceUnlocked` and never
/// calls the other two itself; and a real closure over `MintGrant` really plugs into
/// `DelveLoot.RollRoom`'s own `mintAt: Func&lt;LootGrant, int, LootMintResult&gt;` parameter end to
/// end, against the real shipped rarity ladder and drop-volume tuning.
/// </summary>
public class MintGrantStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public MintGrantStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    static AtomRow VitalityAtom => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", 1),
        KindId = "stat.modify", FamilyId = "atom.vitality", Variant = "", Tier = 1,
        Name = "atom.vitality", ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
    };

    // ---- Unique arm: real, persisted container -----------------------------------------------------

    [Fact]
    public void MintGrant_for_Unique_persists_a_real_instance_and_returns_its_id()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        // No Rarity set -- UpsertContainer validates a non-empty Rarity against the imported rarity
        // ladder (ContainerValidator.Validate's own rarityExists check), and this fresh test store
        // imports none; Rarity is not load-bearing for what this test proves, matching
        // QuestRewardBankingComposabilityTests.cs's own identical container fixture, which sets none.
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.a-real-unique",
            Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, VitalityAtom.AtomId) },
        }).IsOk);

        var grant = new LootGrant(0, DropEntryKind.Unique, "item.a-real-unique", 1, AffixChannels.Drop, RollSeed: 42);

        var result = _store.MintGrant(grant, thetaContent: 20, roleFamilyCells: Array.Empty<RoleFamilyCell>(), Tuning);

        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        Assert.NotNull(result.InstanceId);
        var instance = _store.GetInstance(result.InstanceId!);
        Assert.NotNull(instance);
        Assert.Equal("item.a-real-unique", instance!.ContainerId);
        Assert.Single(instance.Atoms);
        Assert.Equal(VitalityAtom.AtomId, instance.Atoms[0].AtomId);
    }

    [Fact]
    public void MintGrant_for_an_unresolvable_Unique_ref_refuses_and_persists_nothing()
    {
        var grant = new LootGrant(0, DropEntryKind.Unique, "item.does-not-exist", 1, AffixChannels.Drop, RollSeed: 1);

        var result = _store.MintGrant(grant, 20, Array.Empty<RoleFamilyCell>(), Tuning);

        Assert.False(result.Rejection.IsOk);
        Assert.Contains("drop.unknown-unique", result.Rejection.Detail);
        Assert.Null(result.InstanceId);
    }

    // ---- Equipment arm: real atom catalog + caller-supplied role-family cells -----------------------

    [Fact]
    public void MintGrant_for_Equipment_persists_a_real_instance_via_the_derived_role_family_table()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        var cells = new[] { new RoleFamilyCell("footing", "humanoid", "atom.vitality", MaxTier: 3) };
        var grant = new LootGrant(0, DropEntryKind.Equipment, "", 1, AffixChannels.Drop,
            BaseTypeId: "item.humanoid-feet-a-001", Frame: "humanoid", Role: "footing",
            MinTier: 1, MaxTier: 1, PrefixRolls: 1, RollSeed: 7);

        var result = _store.MintGrant(grant, thetaContent: 20, cells, Tuning);

        Assert.True(result.Rejection.IsOk, result.Rejection.ToString());
        Assert.NotNull(result.InstanceId);
        var instance = _store.GetInstance(result.InstanceId!);
        Assert.NotNull(instance);
        Assert.StartsWith("item.drop-humanoid-feet-a-001-", instance!.ContainerId, StringComparison.Ordinal);
        Assert.NotEmpty(instance.Atoms);
    }

    // ---- the persistence boundary: mintAt stops at SaveInstanceUnlocked -----------------------------

    [Fact]
    public void MintGrant_never_acquires_ownership_or_writes_the_drop_log_itself()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.a-real-unique-2",
            Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, VitalityAtom.AtomId) },
        }).IsOk);
        var grant = new LootGrant(0, DropEntryKind.Unique, "item.a-real-unique-2", 1, AffixChannels.Drop, RollSeed: 3);

        var result = _store.MintGrant(grant, 20, Array.Empty<RoleFamilyCell>(), Tuning);

        Assert.True(result.Rejection.IsOk);
        Assert.NotNull(_store.GetInstance(result.InstanceId!)); // the instance itself IS persisted...
        Assert.Null(_store.GetItem(result.InstanceId!));         // ...but ownership is not acquired...
        Assert.Empty(_store.ListDropLog("anyone", 10));          // ...and the drop log is not written
    }

    [Fact]
    public void MintGrant_refuses_an_unsupported_kind_without_writing_anything()
    {
        var grant = new LootGrant(0, DropEntryKind.Material, "mat.iron", 5, AffixChannels.Drop, RollSeed: 1);

        var result = _store.MintGrant(grant, 20, Array.Empty<RoleFamilyCell>(), Tuning);

        Assert.False(result.Rejection.IsOk);
        Assert.Contains("drop.mint-kind-unsupported", result.Rejection.Detail);
        Assert.Null(result.InstanceId);
    }

    // ---- MintGrantUnlocked composability, mirroring QuestRewardBankingComposabilityTests exactly ----

    [Fact]
    public void MintGrantUnlocked_composes_into_one_externally_owned_transaction_with_AcquireItem_and_PersistLoot()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.a-real-unique-3",
            Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, VitalityAtom.AtomId) },
        }).IsOk);
        var grant = new LootGrant(0, DropEntryKind.Unique, "item.a-real-unique-3", 1, AffixChannels.Drop, RollSeed: 9);
        var manifest = new LootManifest("compose-test-1", "drop.dungeon.fire.cache", 0x51EED, 20,
            Array.Empty<LootGrant>(), Array.Empty<string>(), "{}",
            new LootPityState(0, 0), new LootPityState(0, 0), null, false, null);

        LootMintResult minted;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            minted = _store.MintGrantUnlocked(db, tx, grant, 20, Array.Empty<RoleFamilyCell>(), Tuning);
            Assert.True(minted.Rejection.IsOk, minted.Rejection.ToString());

            var item = new RpgItemRow
            {
                InstanceId = minted.InstanceId!, PlayerId = "p1",
                AcquiredUtc = "2026-09-07T00:00:00Z", OriginKind = "delve-room",
            };
            var acquireRejection = _store.AcquireItemUnlocked(db, tx, item);
            Assert.True(acquireRejection.IsOk, acquireRejection.ToString());
            _store.PersistLootUnlocked(db, tx, "p1", manifest, "dungeon-room", "delve-1:2:3", 0, 0, Array.Empty<ItemGenerationRow>());
            tx.Commit();
        }

        // Every write landed, through the store's own PUBLIC read surface.
        Assert.NotNull(_store.GetInstance(minted.InstanceId!));
        Assert.NotNull(_store.GetItem(minted.InstanceId!));
        Assert.Contains(_store.ListDropLog("p1", 10), r => r.CorrelationId == "compose-test-1");
    }

    [Fact]
    public void MintGrantUnlocked_never_secretly_commits_its_own_transaction()
    {
        Assert.True(_store.UpsertAtom(VitalityAtom).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.a-real-unique-4",
            Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(0, VitalityAtom.AtomId) },
        }).IsOk);
        var grant = new LootGrant(0, DropEntryKind.Unique, "item.a-real-unique-4", 1, AffixChannels.Drop, RollSeed: 10);

        LootMintResult minted;
        using (var db = SqliteConnectionFactory.Open(_store.HotPath))
        using (var tx = db.BeginTransaction())
        {
            minted = _store.MintGrantUnlocked(db, tx, grant, 20, Array.Empty<RoleFamilyCell>(), Tuning);
            Assert.True(minted.Rejection.IsOk);
            // deliberately never committing -- a rollback must leave nothing behind.
        }

        Assert.Null(_store.GetInstance(minted.InstanceId!));
    }

    // ---- guards --------------------------------------------------------------------------------------

    [Fact]
    public void MintGrantUnlocked_null_arguments_throw()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var tx = db.BeginTransaction();
        var grant = new LootGrant(0, DropEntryKind.Material, "x", 1, AffixChannels.Drop);
        Assert.Throws<ArgumentNullException>(() =>
            _store.MintGrantUnlocked(db, tx, null!, 20, Array.Empty<RoleFamilyCell>(), Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            _store.MintGrantUnlocked(db, tx, grant, 20, null!, Tuning));
        Assert.Throws<ArgumentNullException>(() =>
            _store.MintGrantUnlocked(db, tx, grant, 20, Array.Empty<RoleFamilyCell>(), null!));
    }

    // ---- the real, direct end-to-end proof: a real MintGrant closure plugs into RollRoom today ------

    /// <summary>Twelve small, hand-built families spanning every real tier (D29's ladder: 1..5) and
    /// both derived affix classes (half carry a trigger, so `AffixValidator.AffixClassOfAtom` resolves
    /// them Suffix; half do not, resolving Prefix) -- generous enough that whichever real rarity rung
    /// the seeded draw actually lands on, the pool has enough distinct drawable groups on both budgets,
    /// without asserting on (or depending on) which rung that happens to be.</summary>
    static (IReadOnlyList<RoleFamilyCell> Cells, IReadOnlyList<AtomRow> Atoms) WideOpenFooting()
    {
        var cells = new List<RoleFamilyCell>();
        var atoms = new List<AtomRow>();
        for (var i = 0; i < 6; i++)
        {
            var prefixFamily = $"atom.e2e-prefix-{i}";
            var suffixFamily = $"atom.e2e-suffix-{i}";
            cells.Add(new RoleFamilyCell("footing", "humanoid", prefixFamily, MaxTier: 5));
            cells.Add(new RoleFamilyCell("footing", "humanoid", suffixFamily, MaxTier: 5));
            for (var tier = 1; tier <= 5; tier++)
            {
                atoms.Add(new AtomRow
                {
                    AtomId = AtomRow.DeriveId(prefixFamily, "", tier), KindId = "stat.modify",
                    FamilyId = prefixFamily, Variant = "", Tier = tier, Name = prefixFamily,
                    ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
                });
                atoms.Add(new AtomRow
                {
                    AtomId = AtomRow.DeriveId(suffixFamily, "", tier), KindId = "stat.modify",
                    FamilyId = suffixFamily, Variant = "", Tier = tier, Name = suffixFamily,
                    WhenJson = "{\"trigger\":\"OnDamageDealt\"}",
                    ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":10}",
                });
            }
        }
        return (cells, atoms);
    }

    [Fact]
    public void A_real_MintGrant_closure_matches_RollRooms_own_mintAt_shape_and_really_mints_through_it()
    {
        var (cells, atoms) = WideOpenFooting();
        foreach (var atom in atoms) Assert.True(_store.UpsertAtom(atom).IsOk, atom.AtomId);

        // The exact shape DelveLoot.RollRoom/RollQuestReward's own `mintAt` parameter requires --
        // Func<LootGrant, int, LootMintResult> -- built directly from the real store, real corpus.
        Func<LootGrant, int, LootMintResult> mintAt = (grant, theta) => _store.MintGrant(grant, theta, cells, Tuning);

        var table = new DropTableRow("drop.test.footing", SourceAllow: new[] { "web" }, MinIlvl: null, MaxIlvl: null,
            Enabled: true, Revision: 1, Groups: new[]
            {
                new DropTableGroupRow("main", Seq: 0, Rolls: 1, Entries: new[]
                {
                    new DropTableEntryRow(Seq: 0, Kind: DropEntryKind.Equipment, RefId: "", Weight: 100,
                        Frame: "humanoid", Role: "footing"),
                }),
            });
        var view = new LootContentView(
            Sources: new Dictionary<string, LootSourceRow>(),
            Tables: new Dictionary<string, DropTableRow> { [table.TableId] = table },
            Ladder: RealLadder(),
            BaseTypesFor: (_, _) => new[] { "item.humanoid-feet-a-001" });

        var room = new RoomLootInput(
            PlayerId: "p1", SourceKind: "dungeon-room", SourceId: "delve-e2e:1:1", TableId: table.TableId,
            Row: 1, Col: 1, DelveSeed: 0xABCDEF, ThetaRoom: 20, ThetaActor: 20, Rung: IdentityRung());

        var rejection = DelveLoot.RollRoom(room, view, RealDropVolumeTuning(), LootPityState.Empty, mintAt, out var result);

        Assert.True(rejection.IsOk, rejection.ToString());
        Assert.NotNull(result);
        Assert.NotEmpty(result!.Manifest.Grants);
        var grant = Assert.Single(result.Manifest.Grants);
        Assert.NotNull(grant.InstanceId);
        // Really persisted, through the real store -- not just returned in memory.
        Assert.NotNull(_store.GetInstance(grant.InstanceId!));
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CONTRIBUTING.md"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    static IReadOnlyList<RarityRung> RealLadder()
    {
        var tuning = ItemRarityTuning.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-rarity.v1.json")));
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(RepoRoot(), "data", "seed", "rarity", "ladder.v1.json")));

        var rungs = new List<RarityRung>();
        foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
        {
            var id = e.GetProperty("id").GetString()!;
            rungs.Add(new RarityRung(
                id, e.GetProperty("ordinal").GetInt32(),
                e.GetProperty("prefixRolls").GetInt32(), e.GetProperty("suffixRolls").GetInt32(),
                e.GetProperty("minTier").GetInt32(), e.GetProperty("maxTier").GetInt32(),
                tuning[id].DropWeightPer100k));
        }
        return rungs;
    }

    static DropVolumeTuning RealDropVolumeTuning() => DropVolumeTuning.Parse(
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "item-drop-volume.v1.json")));

    static DifficultyRungTuning IdentityRung() => new(
        BandDelta: 0, EliteWeightMultMilli: 1000, RestWeightMultMilli: 1000, RestHealMultMilli: 1000,
        HungerMultMilli: 1000, SpiritDrainMultMilli: 1000, MerchantMarkupMultMilli: 1000,
        WildDispositionShiftRungs: 0, EnemyCountDeltaFight: 0, EnemyCountDeltaElite: 0,
        BossRetinuePerPartyDelta: 0, BossWDelta: 0, ProvisionCellsDelta: 0,
        RestEveryOtherRow: false, RestRowsOnlyBeforeBoss: false, DoubleBoss: false,
        EventSeverityTier: 2, EliteKitTier: 0, BossKitTier: 0,
        UnknownPityStepMultMilliCache: 1000, UnknownPityStepMultMilliMerchant: 1000, UnknownPityStepMultMilliFight: 1000,
        RarityFloor: null, RarityShiftRungs: 0);
}
