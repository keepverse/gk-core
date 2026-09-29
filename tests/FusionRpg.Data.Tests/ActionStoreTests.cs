using FusionRpg.Core.Actions;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data.Sqlite;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T1/T2/T3 (action-todo.md, spec-action-model.md §9). The validator has its own tests in Core;
/// this covers what only exists once a database is involved — round trips, the closed schema on
/// `rpg_action_grant`, ordinal resolution order, and a withdraw that only touches its own source.
/// </summary>
public class ActionStoreTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly string _realAtomId;

    public ActionStoreTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _realAtomId = SeedContainerAndAtom("skill.test", "atom.real");
        SeedContainerAndAtom("skill.act-attack-container", "atom.attack-hit");
        SeedContainerAndAtom("skill.act-guard-container", "atom.guard-status");
        SeedContainerAndAtom("skill.act-move-container", "atom.move-noop");
        SeedContainerAndAtom("skill.innate-rot-burst-container", "atom.rot-burst");
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Returns the derived atom id actually stored, since <c>AtomRow.DeriveId</c> owns the grammar.</summary>
    string SeedContainerAndAtom(string containerId, string family)
    {
        var atomId = AtomRow.DeriveId(family, "", 1);
        var atomResult = _store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = family,
            Variant = "",
            Tier = 1,
            Name = family,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        });
        Assert.True(atomResult.IsOk, atomResult.ToString());

        var containerResult = _store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        });
        Assert.True(containerResult.IsOk, containerResult.ToString());
        return atomId;
    }

    static ActionRow Basic(string id, string containerId) => new()
    {
        ActionId = id,
        Name = id,
        Kind = ActionKind.Basic,
        ContainerId = containerId,
    };

    static ActionRow Skill(string id, string containerId, bool grantable = true,
        bool defaultAttackEligible = false, ActionTargetSpec? targeting = null) => new()
    {
        ActionId = id,
        Name = id,
        Kind = ActionKind.Skill,
        ContainerId = containerId,
        Grantable = grantable,
        DefaultAttackEligible = defaultAttackEligible,
        Targeting = targeting ?? new ActionTargetSpec(),
        Tags = new[] { ActionTag.Offensive },
    };

    // ---- round trip --------------------------------------------------------------------------------

    [Fact]
    public void An_action_row_round_trips_identically()
    {
        var row = Skill("skill.fireball", "skill.test") with
        {
            Rung = 3,
            Envelope = FusionRpg.Core.Battle.Timeline.ActionEnvelope.NoOp with
            {
                ActionId = "skill.fireball", TimeCostTicks = 500, CooldownTicks = 1200,
                CooldownKey = "cd.fireball", CooldownChannel = "skill.cooldown.attack",
            },
        };

        var write = _store.UpsertAction(row);
        Assert.True(write.IsOk, write.ToString());

        var read = _store.GetAction("skill.fireball");
        Assert.NotNull(read);
        Assert.Equal(row.ActionId, read!.ActionId);
        Assert.Equal(row.Kind, read.Kind);
        Assert.Equal(row.Rung, read.Rung);
        Assert.Equal(row.Grantable, read.Grantable);
        Assert.Equal(row.Tags, read.Tags);
        Assert.Equal(row.Envelope.TimeCostTicks, read.Envelope.TimeCostTicks);
        Assert.Equal(row.Envelope.CooldownTicks, read.Envelope.CooldownTicks);
        Assert.Equal(row.Envelope.CooldownKey, read.Envelope.CooldownKey);
        Assert.Equal(row.Envelope.CooldownChannel, read.Envelope.CooldownChannel);
    }

    [Fact]
    public void An_unknown_container_id_is_rejected_by_the_store()
    {
        var result = _store.UpsertAction(Skill("skill.ghost", "skill.nonexistent"));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.UnknownContainer, result.Reason);
    }

    [Fact]
    public void An_area_action_is_rejected_by_the_store_with_no_board()
    {
        var targeting = new ActionTargetSpec { Mode = ActionTargetMode.Area };
        var result = _store.UpsertAction(Skill("skill.nova", "skill.test", targeting: targeting));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.AreaRequiresBoard, result.Reason);
    }

    [Fact]
    public void Repeated_writes_bump_revision()
    {
        var row = Skill("skill.fireball", "skill.test");
        _store.UpsertAction(row);
        var first = _store.GetAction("skill.fireball")!.Revision;
        _store.UpsertAction(row with { Name = "Fireball II" });
        var second = _store.GetAction("skill.fireball")!.Revision;
        Assert.True(second > first);
    }

    // ---- rpg_action_cost -----------------------------------------------------------------------------

    [Fact]
    public void A_cost_row_round_trips_and_all_six_resources_are_reachable()
    {
        _store.UpsertAction(Skill("skill.fireball", "skill.test"));

        foreach (var resourceId in FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds)
        {
            var write = _store.UpsertCost(new ActionCostRow(
                "skill.fireball", resourceId, ValueSpec.Of(10), ActionCostTiming.OnCommit));
            Assert.True(write.IsOk, write.ToString());
        }

        var costs = _store.ListCosts("skill.fireball");
        Assert.Equal(6, costs.Count);
        Assert.All(costs, c => Assert.Equal(ValueSpec.Of(10), c.AmountSpec));
    }

    [Fact]
    public void An_unknown_resource_is_rejected_by_the_store()
    {
        _store.UpsertAction(Skill("skill.fireball", "skill.test"));
        var result = _store.UpsertCost(new ActionCostRow(
            "skill.fireball", "mana", ValueSpec.Of(10), ActionCostTiming.OnCommit));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.UnknownResource, result.Reason);
    }

    // ---- rpg_action_effect_scope ----------------------------------------------------------------------

    [Fact]
    public void An_atom_with_no_scope_row_defaults_to_each_target()
    {
        _store.UpsertAction(Skill("skill.fireball", "skill.test"));
        Assert.Equal(ActionEffectScope.EachTarget, _store.GetScope("skill.fireball", _realAtomId));
    }

    [Fact]
    public void A_scope_row_round_trips_and_overrides_the_default()
    {
        _store.UpsertAction(Skill("skill.fireball", "skill.test"));
        var write = _store.UpsertScope(new ActionScopeRow("skill.fireball", _realAtomId, ActionEffectScope.Caster));
        Assert.True(write.IsOk, write.ToString());
        Assert.Equal(ActionEffectScope.Caster, _store.GetScope("skill.fireball", _realAtomId));
    }

    [Fact]
    public void A_scope_naming_an_atom_the_container_lacks_is_rejected_by_the_store()
    {
        _store.UpsertAction(Skill("skill.fireball", "skill.test"));
        var result = _store.UpsertScope(new ActionScopeRow("skill.fireball", "atom.ghost", ActionEffectScope.Caster));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.ScopeAtomNotInContainer, result.Reason);
    }

    // ---- rpg_action_grant ------------------------------------------------------------------------------

    [Fact]
    public void The_grant_table_has_no_instance_id_column()
    {
        // The correction from item/ssot-granted-actions.md §5.5 item 5, made unforgettable: a
        // granted action has no instance and no rolls, so `effect_binding` cannot be reused. Asserted
        // directly on the live schema, not the store's own C# API surface.
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_action_grant);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(1));

        Assert.DoesNotContain("instance_id", columns);
        Assert.Contains("owner_kind", columns);
        Assert.Contains("owner_key", columns);
        Assert.Contains("action_id", columns);
        Assert.Contains("source", columns);
    }

    [Fact]
    public void The_grant_table_is_closed_no_magnitude_envelope_cost_or_target_column_can_sneak_in()
    {
        // T24, spec-grant-seam.md S6, item 9: "a grant names an action_id, a source and a grant_role.
        // It carries no magnitude, no envelope field, no cost row, no target spec." An ADDITIVE
        // column check (asserting a handful of forbidden names are absent) would miss a column named
        // something the author of this test never thought to list -- this asserts the CLOSED set
        // instead, so ANY future column addition fails here first, forcing a deliberate re-read of
        // item 9 rather than a silent schema drift into "a second action system."
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(rpg_action_grant);";
        using var r = cmd.ExecuteReader();

        var columns = new List<string>();
        while (r.Read()) columns.Add(r.GetString(1));
        columns.Sort(StringComparer.Ordinal);

        var expected = new[] { "action_id", "grant_id", "grant_role", "owner_key", "owner_kind", "source" };
        Array.Sort(expected, StringComparer.Ordinal);

        Assert.Equal(expected, columns);
    }

    [Fact]
    public void A_grant_colliding_with_a_basic_is_rejected_and_never_double_counted()
    {
        _store.UpsertAction(Basic("act.attack", "skill.act-attack-container"));
        var result = _store.UpsertGrant(new ActionGrantRow(OwnerKind.Player, "1", "act.attack", "item.sword"));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.BasicCollision, result.Reason);
        Assert.Empty(_store.ListGrants(new OwnerScope(OwnerKind.Player, "1")));
    }

    [Fact]
    public void A_non_grantable_action_is_refused_at_import()
    {
        _store.UpsertAction(Skill("skill.locked", "skill.test", grantable: false));
        var result = _store.UpsertGrant(new ActionGrantRow(OwnerKind.Player, "1", "skill.locked", "item.trinket"));
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.ActionNotGrantable, result.Reason);
    }

    [Fact]
    public void Grants_resolve_in_action_id_ordinal_order_regardless_of_insert_order()
    {
        _store.UpsertAction(Skill("skill.charlie", "skill.test"));
        _store.UpsertAction(Skill("skill.alpha", "skill.test"));
        _store.UpsertAction(Skill("skill.bravo", "skill.test"));

        var owner = new OwnerScope(OwnerKind.Player, "1");
        // Deliberately shuffled insert order — resolution order must not depend on it.
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.charlie", "item.a"));
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.alpha", "item.b"));
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.bravo", "item.c"));

        var grants = _store.ListGrants(owner);
        Assert.Equal(new[] { "skill.alpha", "skill.bravo", "skill.charlie" },
            grants.Select(g => g.ActionId).ToArray());
    }

    [Fact]
    public void Withdrawing_by_source_removes_only_that_sources_grants()
    {
        _store.UpsertAction(Skill("skill.alpha", "skill.test"));
        _store.UpsertAction(Skill("skill.bravo", "skill.test"));

        var owner = new OwnerScope(OwnerKind.Player, "1");
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.alpha", "item.sword"));
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.bravo", "item.shield"));

        var removed = _store.WithdrawGrantsBySource(owner, "item.sword");
        Assert.Equal(1, removed);

        var remaining = _store.ListGrants(owner);
        Assert.Single(remaining);
        Assert.Equal("skill.bravo", remaining[0].ActionId);
    }

    [Fact]
    public void Two_items_granting_the_same_action_leave_two_rows_but_one_effective_entry()
    {
        _store.UpsertAction(Skill("skill.alpha", "skill.test"));
        var owner = new OwnerScope(OwnerKind.Player, "1");
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.alpha", "item.ring"), "grant-1");
        _store.UpsertGrant(new ActionGrantRow(owner.Kind, owner.Key, "skill.alpha", "item.amulet"), "grant-2");

        var grants = _store.ListGrants(owner);
        Assert.Equal(2, grants.Count);
        Assert.All(grants, g => Assert.Equal("skill.alpha", g.ActionId));

        // Distinct action_ids after dedup is what T23's assembly reads — the store keeps provenance.
        Assert.Single(grants.Select(g => g.ActionId).Distinct());
    }

    // ---- species basics --------------------------------------------------------------------------------

    [Fact]
    public void A_complete_species_basics_row_round_trips()
    {
        _store.UpsertAction(Basic("act.attack", "skill.act-attack-container"));
        _store.UpsertAction(Basic("act.guard", "skill.act-guard-container"));
        _store.UpsertAction(Basic("act.move", "skill.act-move-container"));
        _store.UpsertAction(new ActionRow
        {
            ActionId = "innate.rot-burst", Name = "Rot Burst", Kind = ActionKind.Innate,
            ContainerId = "skill.innate-rot-burst-container",
        });

        var row = new SpeciesBasicsRow("zombie.42", "act.attack", "act.guard", "act.move", "innate.rot-burst");
        var write = _store.UpsertSpeciesBasics(row);
        Assert.True(write.IsOk, write.ToString());

        var read = _store.GetSpeciesBasics("zombie.42");
        Assert.Equal(row, read);
    }

    [Fact]
    public void A_species_row_missing_a_basic_is_rejected_by_the_store()
    {
        var row = new SpeciesBasicsRow("zombie.42", "", "act.guard", "act.move", null);
        var result = _store.UpsertSpeciesBasics(row);
        Assert.False(result.IsOk);
        Assert.Equal(ActionRejectionReason.MissingSpeciesBasic, result.Reason);
        Assert.Null(_store.GetSpeciesBasics("zombie.42"));
    }
}
