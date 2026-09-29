using System.Globalization;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Drops;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests.Gates;

/// <summary>
/// build-preset BP1.7 (spec-item-loadout-apply.md) — the item loadout library's apply half, against
/// a real store, a real bound specimen and real stored items/relics. `ItemEquipEndpointsTests.cs`
/// already proves the equip executor itself over HTTP; these tests prove the NEW dispatch layer:
/// each entry reaches the flow that owns its ref kind, a conflict refuses with nothing written,
/// `force` reports what it stripped, and a Missing entry is reported, never dropped.
/// </summary>
public class ItemLoadoutApplyServiceTests : IDisposable
{
    const string Rung = "cultivated";
    const string BaseTypeId = "item.loadout-apply-base-001";
    const string BladeContainer = "item.loadout-apply-blade";
    const int ItemLevel = 10;
    static readonly string ArmamentPrimary = ItemRoles.Id(ItemRole.ArmamentPrimary);
    static readonly string CoreGuard = ItemRoles.Id(ItemRole.CoreGuard);
    // A real UniqueEquipmentCatalog id locked to the "armor" legacy slot (RelicRowMigrationTests.cs
    // pairs it with "armor" too) -- CoreGuard maps to "armor" (LegacyEquipSlots), so this is the
    // one every CoreGuard-targeted test below needs; a slot-mismatched relic id refuses.
    const string RelicId = "relic.tidewrack_band";

    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly ItemLoadoutApplyService _service;
    readonly long _playerId;
    readonly string _playerKey;
    readonly string _specimenId;

    public ItemLoadoutApplyServiceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _playerKey = _playerId.ToString(CultureInfo.InvariantCulture);

        for (var i = 0; i < RarityLadder.RungIds.Count; i++)
            Assert.True(_store.UpsertRarity(new RarityRow(RarityLadder.RungIds[i], (i + 1) * 10, 3, 0, 1, 5)).Ok);

        _specimenId = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var provider = services.BuildServiceProvider();
        var hub = provider.GetRequiredService<IHubContext<RpgHub>>();
        var equip = new ItemEquipService(_store);
        var unique = new UniqueActorService(_store, new InjectorCommandInbox(), hub);
        _service = new ItemLoadoutApplyService(_store, equip, unique);
    }

    public void Dispose() => _testStore.Dispose();

    string SeedItem(string containerId, ItemRole role, string? playerKey = null)
    {
        var atomId = AtomRow.DeriveId("atom.loadout-apply-vitality", "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.loadout-apply-vitality",
            Variant = "",
            Tier = 1,
            Name = "vitality",
            ParamsJson = """{"channel":"maxHp","op":"flat","amount":10}""",
        }).IsOk);

        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Item,
            Slot = ItemRoles.Id(role),
            Rarity = Rung,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        var instanceId = _store.SaveInstance(new InstanceRow
        {
            ContainerId = containerId,
            RollSeed = 4242,
            CatalogRevision = _store.GetCatalogRevision(),
            Origin = InstanceOrigin.Drop,
            Atoms = new[] { new InstanceAtomRow(0, atomId, """{"amount":10}""") },
        });

        var owner = playerKey ?? _playerKey;
        Assert.True(_store.AcquireItem(new RpgItemRow
        {
            InstanceId = instanceId,
            PlayerId = owner,
            AcquiredUtc = "2026-09-06T00:00:00Z",
            OriginKind = "drop",
        }).IsOk);

        _store.PersistLoot(
            owner,
            new LootManifest("la-drop", "table.loadout-apply", 7UL, ItemLevel, Array.Empty<LootGrant>(),
                Array.Empty<string>(), "{}", LootPityState.Empty, LootPityState.Empty, null, false, null),
            "test", "loadout-apply", _store.GetCatalogRevision(), 1,
            new[]
            {
                new ItemGenerationRow(instanceId, 0, BaseTypeId, RungOrdinal(), ItemLevel, "humanoid",
                    ItemRoles.Id(role), "drop"),
            });

        return instanceId;
    }

    int RungOrdinal() => _store.ListRarities().First(r => r.RarityId == Rung).Ordinal;

    void SaveLoadout(string loadoutId, params RpgItemLoadoutEntryRow[] entries) =>
        _store.SaveLoadout(
            new RpgItemLoadoutRow(loadoutId, _playerKey, "Preset", null, "2026-01-01T00:00:00Z", 0), entries);

    // ---- spec-armoury.md's own named test -----------------------------------------------------

    [Fact]
    public async Task A_loadout_entry_whose_item_was_salvaged_returns_missing()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        // Salvage: the instance and its ownership row are gone, the preset row is not.
        _store.DeleteInstance(instanceId);

        var outcome = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: false);

        Assert.True(outcome.Ok); // the apply itself is not refused by a missing entry
        var role = Assert.Single(outcome.Results);
        Assert.Equal(ArmamentPrimary, role.Role);
        Assert.False(role.Ok);
        Assert.Equal("loadout.entry-missing", role.Reason);
    }

    // ---- spec-armoury.md's other named test, plus force -----------------------------------------

    [Fact]
    public async Task Applying_a_loadout_whose_item_is_held_elsewhere_refuses_with_LoadoutConflict()
    {
        var otherSpecimen = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _store.SaveAssignment(otherSpecimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId, "2026-01-01T00:00:00Z");
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        var preview = _service.Preview(_playerId, "lo-1", _specimenId, force: false);
        Assert.True(preview.Refused);
        Assert.Single(preview.Loadout.Conflicts);
        Assert.Equal(otherSpecimen, preview.Loadout.Conflicts[0].HeldBy.SpecimenId);

        var outcome = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: false);

        Assert.False(outcome.Ok);
        Assert.Empty(outcome.Results); // nothing was applied
        // Nothing written: the contested copy is still on the other specimen, not on the target.
        Assert.Empty(_store.ListAssignments(_specimenId));
        Assert.Single(_store.ListAssignments(otherSpecimen));
    }

    [Fact]
    public async Task Force_reports_what_it_stripped()
    {
        var otherSpecimen = _store.CreateUniqueActor(_playerId, "plant", 1).InstanceId;
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _store.SaveAssignment(otherSpecimen, ItemRole.ArmamentPrimary, EquipRefKinds.Rolled, instanceId, "2026-01-01T00:00:00Z");
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        var outcome = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: true);

        Assert.True(outcome.Ok);
        var stripped = Assert.Single(outcome.Plan.Loadout.Stripped);
        Assert.Equal(otherSpecimen, stripped.SpecimenId);
        Assert.Equal(ArmamentPrimary, stripped.Role);

        // The strip actually happened: the other specimen no longer wears it, the target does.
        Assert.Empty(_store.ListAssignments(otherSpecimen));
        Assert.Contains(_store.ListAssignments(_specimenId), a => a.RefId == instanceId);
    }

    // ---- each kind reaches its own flow, and never the other's ----------------------------------

    [Fact]
    public async Task A_rolled_entry_and_a_stock_entry_each_reach_their_own_flow_and_never_the_others()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        _store.AdjustStock(_playerKey, RelicId, delta: 1);
        SaveLoadout("lo-1",
            new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId),
            new RpgItemLoadoutEntryRow("lo-1", CoreGuard, "stock", RelicId));

        var outcome = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: false);

        Assert.True(outcome.Ok);
        Assert.All(outcome.Results, r => Assert.True(r.Ok, $"{r.Role}: {r.Reason}"));

        // Both flows share rpg_item_assignment (the 2026-09-06 row migration, D1 §10 M1) -- "neither
        // writes the other's kind" means ref_kind, not table membership: the rolled entry's role
        // carries "rolled", the stock entry's role carries "stock", and neither role carries both.
        var assignments = _store.ListAssignments(_specimenId);
        Assert.Contains(assignments, a => a.Role == ItemRole.ArmamentPrimary && a.RefKind == EquipRefKinds.Rolled && a.RefId == instanceId);
        Assert.Contains(assignments, a => a.Role == ItemRole.CoreGuard && a.RefKind == EquipRefKinds.Stock && a.RefId == RelicId);
        Assert.DoesNotContain(assignments, a => a.Role == ItemRole.ArmamentPrimary && a.RefKind == EquipRefKinds.Stock);
        Assert.DoesNotContain(assignments, a => a.Role == ItemRole.CoreGuard && a.RefKind == EquipRefKinds.Rolled);

        // The stock entry also landed in the relic wire's own equipment list, which is what
        // ItemEquipService itself never writes.
        var equipment = _store.GetUniqueEquipment(_specimenId);
        Assert.NotNull(equipment);
        Assert.Contains(equipment!.Items, e => e.Slot == "armor" && e.ItemId == RelicId);
    }

    // ---- commander target ------------------------------------------------------------------------

    [Fact]
    public async Task A_commander_target_lands_in_the_player_scope_table_exactly_as_a_direct_equip_would()
    {
        var commanderId = _store.GetDefaultLawnCommanderId(_playerId);
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        var outcome = await _service.ApplyAsync(_playerId, "lo-1", commanderId, force: false);

        Assert.True(outcome.Ok);
        Assert.True(Assert.Single(outcome.Results).Ok);
        Assert.Contains(_store.ListPlayerItemAssignments(_playerKey),
            a => a.Role == ItemRole.ArmamentPrimary && a.RefId == instanceId);
        // Never the specimen-scope table for a commander target.
        Assert.Empty(_store.ListAssignments(_specimenId));
    }

    // ---- idempotent --------------------------------------------------------------------------------

    [Fact]
    public async Task Applying_the_same_loadout_twice_leaves_the_same_rows_and_reports_Ok_both_times()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        var first = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: false);
        var second = await _service.ApplyAsync(_playerId, "lo-1", _specimenId, force: false);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.True(Assert.Single(second.Results).Ok);
        Assert.Single(_store.ListAssignments(_specimenId));
    }

    // ---- stock refusals are found at PREVIEW, before anything is written -------------------------

    [Fact]
    public void A_stock_entry_whose_role_has_no_legacy_slot_refuses_at_preview()
    {
        // JewelMinorB has no legacy-slot mapping (only ArmamentPrimary/CoreGuard/JewelMinorA do).
        var role = ItemRoles.Id(ItemRole.JewelMinorB);
        _store.AdjustStock(_playerKey, RelicId, delta: 1);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", role, "stock", RelicId));

        var preview = _service.Preview(_playerId, "lo-1", _specimenId, force: false);

        Assert.True(preview.Refused);
        var refusal = Assert.Single(preview.StockRefusals);
        Assert.Equal("loadout.stock-role-unmapped", refusal.Reason);
    }

    [Fact]
    public void A_stock_entry_aimed_at_the_commander_refuses_at_preview()
    {
        var commanderId = _store.GetDefaultLawnCommanderId(_playerId);
        _store.AdjustStock(_playerKey, RelicId, delta: 1);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", CoreGuard, "stock", RelicId));

        var preview = _service.Preview(_playerId, "lo-1", commanderId, force: false);

        Assert.True(preview.Refused);
        var refusal = Assert.Single(preview.StockRefusals);
        Assert.Equal("loadout.stock-target-commander", refusal.Reason);
    }

    // ---- preview writes nothing ---------------------------------------------------------------

    [Fact]
    public void Preview_never_writes()
    {
        var instanceId = SeedItem(BladeContainer, ItemRole.ArmamentPrimary);
        SaveLoadout("lo-1", new RpgItemLoadoutEntryRow("lo-1", ArmamentPrimary, "item", instanceId));

        var preview = _service.Preview(_playerId, "lo-1", _specimenId, force: false);

        Assert.False(preview.Refused);
        Assert.Empty(_store.ListAssignments(_specimenId));
    }
}
