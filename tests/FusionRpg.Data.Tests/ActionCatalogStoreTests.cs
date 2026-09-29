using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using Xunit;

namespace FusionRpg.Data.Tests;

/// <summary>
/// T30 (action-todo.md, spec-action-catalog.md R2): the half of the content hash claim that only
/// exists once real rows are involved — a changed action value moves the hash, an unchanged one does
/// not, both directions, and <c>rpg_action_grant</c> (per-player state) stays excluded. Mirrors
/// <c>ContentHashStoreTests</c>'s own setup/dispose/helper shape exactly rather than inventing a
/// second harness for the same database.
/// </summary>
[Trait("VerificationId", "data.action-catalog")]
public class ActionCatalogStoreTests : IDisposable
{
    readonly List<DataTestStore> _stores = new();

    public void Dispose()
    {
        foreach (var s in _stores)
            s.Dispose();
    }

    RpgStore NewStore()
    {
        var test = DataTestStore.Create();
        _stores.Add(test);
        return test.Store;
    }

    static AtomRow Vitality(int amount = 45, int tier = 1) => new()
    {
        AtomId = AtomRow.DeriveId("atom.vitality", "", tier),
        KindId = "stat.modify",
        FamilyId = "atom.vitality",
        Tier = tier,
        Name = $"Vitality t{tier}",
        WhenJson = "{}",
        ParamsJson = $$"""{"channel":"maxHp","op":"flat","amount":{{amount}}}""",
        TagsJson = """{"category":"survivability"}""",
        Enabled = true,
    };

    const string ContainerId = "item.skill-test";

    static ContainerRow Container() => new()
    {
        ContainerId = ContainerId,
        Kind = ContainerKind.Item,
        Slot = "weapon",
        Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId("atom.vitality", "", 1)) },
    };

    static ActionRow Action(string id = "skill.test", int windup = 0) => new()
    {
        ActionId = id,
        Name = "Test",
        Kind = ActionKind.Skill,
        ContainerId = ContainerId,
        Rung = 1,
        Grantable = true,
        Envelope = ActionEnvelope.NoOp with { ActionId = id, WindupTicks = windup },
        Targeting = new ActionTargetSpec(),
    };

    void SeedContainer(RpgStore s)
    {
        var atomRejection = s.UpsertAtom(Vitality());
        Assert.True(atomRejection.IsOk, atomRejection.ToString());
        var containerRejection = s.UpsertContainer(Container());
        Assert.True(containerRejection.IsOk, containerRejection.ToString());
    }

    static string Hash(RpgStore s) => s.ComputeContentHash().Hash;

    [Fact]
    public void An_action_row_participates_in_the_content_hash()
    {
        var s = NewStore();
        SeedContainer(s);
        var before = Hash(s);

        var rejection = s.UpsertAction(Action());
        Assert.True(rejection.IsOk, rejection.ToString());

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void A_changed_action_column_moves_the_hash()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action(windup: 0));
        var before = Hash(s);

        s.UpsertAction(Action(windup: 5));

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void Rewriting_an_identical_action_does_not_move_the_hash()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());
        var before = Hash(s);

        s.UpsertAction(Action()); // byte-identical row, re-written

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void A_changed_action_cost_moves_the_hash()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());
        s.UpsertCost(new ActionCostRow("skill.test", "stamina", new ValueSpec(10, 10, RollPolicy.Fixed), ActionCostTiming.OnCommit));
        var before = Hash(s);

        s.UpsertCost(new ActionCostRow("skill.test", "stamina", new ValueSpec(20, 20, RollPolicy.Fixed), ActionCostTiming.OnCommit));

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void A_changed_action_scope_moves_the_hash()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());
        s.UpsertScope(new ActionScopeRow("skill.test", AtomRow.DeriveId("atom.vitality", "", 1), ActionEffectScope.EachTarget));
        var before = Hash(s);

        s.UpsertScope(new ActionScopeRow("skill.test", AtomRow.DeriveId("atom.vitality", "", 1), ActionEffectScope.Caster));

        Assert.NotEqual(before, Hash(s));
    }

    [Fact]
    public void A_grant_never_moves_the_hash_it_is_per_player_state()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());
        var before = Hash(s);

        var rejection = s.UpsertGrant(new ActionGrantRow(OwnerKind.Player, "1", "skill.test", "test-source", "skill"));
        Assert.True(rejection.IsOk, rejection.ToString());

        Assert.Equal(before, Hash(s));
    }

    [Fact]
    public void ListScopes_returns_every_row_for_the_action_ordered_by_atom_id()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());
        var atomId = AtomRow.DeriveId("atom.vitality", "", 1);
        s.UpsertScope(new ActionScopeRow("skill.test", atomId, ActionEffectScope.Caster));

        var scopes = s.ListScopes("skill.test");

        var scope = Assert.Single(scopes);
        Assert.Equal("skill.test", scope.ActionId);
        Assert.Equal(atomId, scope.AtomId);
        Assert.Equal(ActionEffectScope.Caster, scope.Scope);
    }

    [Fact]
    public void ListScopes_on_an_action_with_no_rows_is_empty_not_a_default_row()
    {
        var s = NewStore();
        SeedContainer(s);
        s.UpsertAction(Action());

        Assert.Empty(s.ListScopes("skill.test"));
    }
}
