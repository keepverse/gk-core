using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Grants;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests.Items;

/// <summary>
/// `item_granted_action` and the equip → <c>rpg_action_grant</c> wiring (item module 19).
///
/// <para>⭐ These are the tests that close wiring gap (b): <c>RpgStore.UpsertGrant</c> shipped in T1
/// and had ZERO production callers until <see cref="RpgStore.ApplyEquippedGrants"/>, while
/// <c>RpgStore.ListGrants</c> ran live in <c>WebMatchService.EquippedActionIdsFor</c> the whole
/// time.</para>
/// </summary>
public class ItemGrantStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;

    public ItemGrantStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        SeedSkillContainer("skill.test");
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    void SeedSkillContainer(string containerId)
    {
        var atomId = AtomRow.DeriveId("atom.grant-test", "", 1);
        var atom = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom.grant-test",
            Variant = "",
            Tier = 1,
            Name = "grant test",
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atom.IsOk, atom.ToString());

        var container = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(container.IsOk, container.ToString());
    }

    ActionRow SeedAction(string id, bool grantable = true, bool defaultAttackEligible = false)
    {
        var row = new ActionRow
        {
            ActionId = id,
            Name = id,
            Kind = ActionKind.Skill,
            ContainerId = "skill.test",
            Grantable = grantable,
            DefaultAttackEligible = defaultAttackEligible,
            Tags = new[] { ActionTag.Offensive },
            Rung = 3,
        };
        var result = _store.UpsertAction(row);
        Assert.True(result.IsOk, result.ToString());
        return row;
    }

    /// <summary>
    /// ⚠ A REAL specimen id, not a readable placeholder. <c>RpgStore.CreateUniqueActor</c> mints
    /// <c>Guid.NewGuid().ToString("N")</c> — 32 lowercase hex characters — and
    /// <c>OwnerScope.Validate</c> requires exactly that for <see cref="OwnerKind.Entity"/>
    /// ("entity takes lowercase hex with no 0x prefix"). A kebab placeholder like "spec-1" is
    /// refused with <c>BadOwnerKey</c> before a grant is ever written, so testing with one would
    /// prove the opposite of what these tests claim.
    /// </summary>
    const string Spec1 = "0a1b2c3d4e5f60718293a4b5c6d7e8f9";

    const string Spec2 = "ff1e2d3c4b5a69788796a5b4c3d2e1f0";

    static EquipAssignment Assignment(string specimenId, ItemRole role, string containerId) =>
        new(specimenId, role, "stock", containerId, "2026-09-05T00:00:00.0000000Z");

    // ---- the DDL -------------------------------------------------------------------------------

    /// <summary>
    /// ⛔ §5.3's Never list as a SCHEMA test over the shipped DDL, which is where it can actually be
    /// held: six columns, and none of the twenty-odd names someone will one day propose.
    /// </summary>
    [Fact]
    public void The_item_side_carries_no_cooldown_cost_target_or_condition_column()
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(item_granted_action);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(1));
        columns.Sort(StringComparer.Ordinal);

        var expected = new[] { "action_id", "container_id", "enabled", "grant_role", "revision", "seq" };
        Array.Sort(expected, StringComparer.Ordinal);
        Assert.Equal(expected, columns);
    }

    [Fact]
    public void A_grant_row_round_trips_and_upserts_on_container_and_seq()
    {
        var row = new ItemGrantedActionRow("item.brass-nozzle", 0, "skill.spray-cone", ItemGrantRole.DefaultAttack);
        _store.UpsertItemGrantedAction(row);

        var read = Assert.Single(_store.ListItemGrantedActions("item.brass-nozzle"));
        Assert.Equal(row, read);

        _store.UpsertItemGrantedAction(row with { ActionId = "skill.lob-arc", Revision = 2 });
        var updated = Assert.Single(_store.ListItemGrantedActions("item.brass-nozzle"));
        Assert.Equal("skill.lob-arc", updated.ActionId);
        Assert.Equal(2, updated.Revision);
    }

    [Fact]
    public void The_reverse_index_answers_what_grants_this_action()
    {
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-a", 0, "skill.ember", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-b", 0, "skill.ember", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-b", 1, "skill.other", ItemGrantRole.Granted));

        var granting = _store.ListContainersGranting("skill.ember");
        Assert.Equal(new[] { "item.ring-a", "item.ring-b" }, granting.Select(g => g.ContainerId).ToArray());
    }

    [Fact]
    public void Rows_come_back_in_seq_order_regardless_of_insert_order()
    {
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.lash", 2, "skill.c", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.lash", 0, "skill.a", ItemGrantRole.DefaultAttack));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.lash", 1, "skill.b", ItemGrantRole.Granted));

        Assert.Equal(new[] { "skill.a", "skill.b", "skill.c" },
            _store.ListItemGrantedActions("item.lash").Select(r => r.ActionId).ToArray());
    }

    [Fact]
    public void A_removed_row_is_gone_and_leaves_its_siblings()
    {
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.lash", 0, "skill.a", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.lash", 1, "skill.b", ItemGrantRole.Granted));

        Assert.True(_store.RemoveItemGrantedAction("item.lash", 0));
        Assert.False(_store.RemoveItemGrantedAction("item.lash", 0));
        Assert.Equal(new[] { "skill.b" },
            _store.ListItemGrantedActions("item.lash").Select(r => r.ActionId).ToArray());
    }

    // ---- ⭐ the wiring gap, closed --------------------------------------------------------------

    [Fact]
    public void Equipping_an_item_with_a_grant_row_writes_one_action_grant()
    {
        SeedAction("skill.spray-cone", defaultAttackEligible: true);
        _store.UpsertItemGrantedAction(
            new ItemGrantedActionRow("item.brass-nozzle", 0, "skill.spray-cone", ItemGrantRole.DefaultAttack));

        var failures = _store.ApplyEquippedGrants(
            Spec1,
            new[] { Assignment(Spec1, ItemRole.ArmamentPrimary, "item.brass-nozzle") },
            a => a.RefId);

        Assert.Empty(failures);

        var grants = _store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1));
        var grant = Assert.Single(grants);
        Assert.Equal("skill.spray-cone", grant.ActionId);
        Assert.Equal("item.brass-nozzle", grant.Source);
        Assert.Equal(ActionGrantRoles.DefaultAttack, grant.GrantRole);
    }

    [Fact]
    public void Re_applying_the_same_projection_upserts_rather_than_duplicating()
    {
        SeedAction("skill.ember");
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-a", 0, "skill.ember", ItemGrantRole.Granted));

        var assignments = new[] { Assignment(Spec1, ItemRole.JewelMajor, "item.ring-a") };
        _store.ApplyEquippedGrants(Spec1, assignments, a => a.RefId);
        _store.ApplyEquippedGrants(Spec1, assignments, a => a.RefId);
        _store.ApplyEquippedGrants(Spec1, assignments, a => a.RefId);

        Assert.Single(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));
    }

    [Fact]
    public void Unassigning_deletes_by_source_and_leaves_other_grants()
    {
        SeedAction("skill.spray-cone");
        SeedAction("skill.ember");
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.brass-nozzle", 0, "skill.spray-cone", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-a", 0, "skill.ember", ItemGrantRole.Granted));

        _store.ApplyEquippedGrants(Spec1, new[]
        {
            Assignment(Spec1, ItemRole.ArmamentPrimary, "item.brass-nozzle"),
            Assignment(Spec1, ItemRole.JewelMajor, "item.ring-a"),
        }, a => a.RefId);

        Assert.Equal(2, _store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)).Count);

        Assert.Equal(1, _store.WithdrawEquippedGrants(Spec1, "item.brass-nozzle"));

        var left = Assert.Single(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));
        Assert.Equal("skill.ember", left.ActionId);
    }

    /// <summary>Two items granting one action keep TWO provenance rows, and removing one leaves the
    /// action — "provenance is rows; the set is a group-by" (§3.7a), proven through the store.</summary>
    [Fact]
    public void Two_items_granting_one_action_keep_two_rows_and_removing_one_leaves_the_action()
    {
        SeedAction("skill.ember");
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-a", 0, "skill.ember", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-b", 0, "skill.ember", ItemGrantRole.Granted));

        _store.ApplyEquippedGrants(Spec1, new[]
        {
            Assignment(Spec1, ItemRole.JewelMinorA, "item.ring-a"),
            Assignment(Spec1, ItemRole.JewelMinorB, "item.ring-b"),
        }, a => a.RefId);

        var owner = new OwnerScope(OwnerKind.Entity, Spec1);
        Assert.Equal(2, _store.ListGrants(owner).Count);

        _store.WithdrawEquippedGrants(Spec1, "item.ring-a");

        var left = Assert.Single(_store.ListGrants(owner));
        Assert.Equal("skill.ember", left.ActionId);
        Assert.Equal("item.ring-b", left.Source);

        // ...and the SHIPPED assembler still resolves one entry from it.
        var set = ActionSetAssembler.Assemble(
            new SpeciesBasicsRow("species.pea", "act.attack", "act.guard", "act.move", null),
            _store.ListGrants(owner), _ => true);
        Assert.Single(set.Actions.Where(a => a.ActionId == "skill.ember"));
    }

    /// <summary>
    /// ⛔ The write path's own refusals are RETURNED, not swallowed. This is also X3's real shape at
    /// the store: with no <c>rpg_action</c> row, a grant cannot be written at all.
    /// </summary>
    [Fact]
    public void A_grant_naming_an_action_that_does_not_exist_refuses_and_writes_nothing()
    {
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ghost", 0, "skill.nowhere", ItemGrantRole.Granted));

        var failures = _store.ApplyEquippedGrants(
            Spec1, new[] { Assignment(Spec1, ItemRole.ArmamentPrimary, "item.ghost") }, a => a.RefId);

        var failure = Assert.Single(failures);
        Assert.Equal(ActionRejectionReason.UnknownContainer, failure.Reason);
        Assert.Empty(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));
    }

    [Fact]
    public void A_non_grantable_action_is_refused_by_the_shipped_validator_on_the_write_path()
    {
        SeedAction("skill.locked", grantable: false);
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.x", 0, "skill.locked", ItemGrantRole.Granted));

        var failures = _store.ApplyEquippedGrants(
            Spec1, new[] { Assignment(Spec1, ItemRole.ArmamentPrimary, "item.x") }, a => a.RefId);

        Assert.Equal(ActionRejectionReason.ActionNotGrantable, Assert.Single(failures).Reason);
    }

    [Fact]
    public void A_disabled_grant_row_writes_no_action_grant()
    {
        SeedAction("skill.retired");
        _store.UpsertItemGrantedAction(
            new ItemGrantedActionRow("item.x", 0, "skill.retired", ItemGrantRole.Granted, Enabled: false));

        var failures = _store.ApplyEquippedGrants(
            Spec1, new[] { Assignment(Spec1, ItemRole.ArmamentPrimary, "item.x") }, a => a.RefId);

        Assert.Empty(failures);
        Assert.Empty(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));

        // ...and the row is still there. Content is disabled, never deleted.
        Assert.Single(_store.ListItemGrantedActions("item.x"));
    }

    /// <summary>Two specimens of one species carry independent grant sets — the reason the shipped
    /// reader keys on the instance id and not the player.</summary>
    [Fact]
    public void Grants_are_scoped_per_specimen_not_per_player()
    {
        SeedAction("skill.ember");
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.ring-a", 0, "skill.ember", ItemGrantRole.Granted));

        _store.ApplyEquippedGrants(Spec1,
            new[] { Assignment(Spec1, ItemRole.JewelMajor, "item.ring-a") }, a => a.RefId);

        Assert.Single(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));
        Assert.Empty(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec2)));
    }

    /// <summary>A content edit that REMOVES a grant row converges on the next apply rather than
    /// leaving an orphan — the whole reason the projection withdraws by source first.</summary>
    [Fact]
    public void A_grant_row_removed_from_the_base_type_disappears_on_the_next_apply()
    {
        SeedAction("skill.a");
        SeedAction("skill.b");
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.x", 0, "skill.a", ItemGrantRole.Granted));
        _store.UpsertItemGrantedAction(new ItemGrantedActionRow("item.x", 1, "skill.b", ItemGrantRole.Granted));

        var assignments = new[] { Assignment(Spec1, ItemRole.ArmamentPrimary, "item.x") };
        _store.ApplyEquippedGrants(Spec1, assignments, a => a.RefId);
        Assert.Equal(2, _store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)).Count);

        _store.RemoveItemGrantedAction("item.x", 1);
        _store.ApplyEquippedGrants(Spec1, assignments, a => a.RefId);

        var left = Assert.Single(_store.ListGrants(new OwnerScope(OwnerKind.Entity, Spec1)));
        Assert.Equal("skill.a", left.ActionId);
    }
}
