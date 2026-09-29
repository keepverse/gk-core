using FusionRpg.Core.Actions;
using FusionRpg.Core.Actions.Loadout;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Data;
using FusionRpg.Server.Gates;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests.Gates;

/// <summary>
/// build-preset BP1.3 (spec-gate-services.md) — proves <see cref="ActionLoadoutService.Set"/> and
/// <see cref="ActionLoadoutService.Preview"/> share exactly one held/mid-run predicate pair, the
/// same one `LoadoutEndpoints.cs`'s `POST /api/loadout` route used inline before this lift.
/// `LoadoutEndpointsTests.cs` proves the route itself needed no edit (the route now constructs
/// this service internally rather than taking it as a DI parameter, so its own isolated host,
/// which registers only <see cref="RpgStore"/>, is unaffected); this suite proves the extracted
/// service, so a future build-preset skills applier previewing through it gets the identical
/// rules `Set` then enforces.
/// </summary>
public class ActionLoadoutServiceTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly ActionLoadoutService _service;
    const long PlayerId = 1;

    public ActionLoadoutServiceTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _service = new ActionLoadoutService(_store);
    }

    public void Dispose() => _testStore.Dispose();

    string SeedHeldAction(string actionId)
    {
        var atomId = AtomRow.DeriveId("atom." + actionId.Replace('.', '-'), "", 1);
        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = atomId,
            KindId = "stat.modify",
            FamilyId = "atom." + actionId.Replace('.', '-'),
            Variant = "",
            Tier = 1,
            Name = actionId,
            ParamsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":1}",
        }).IsOk);

        var containerId = "skill." + actionId.Replace('.', '-') + "-container";
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Skill,
            Atoms = new[] { new ContainerAtomRow(0, atomId) },
        }).IsOk);

        Assert.True(_store.UpsertAction(new ActionRow
        {
            ActionId = actionId,
            Name = actionId,
            Kind = ActionKind.Skill,
            ContainerId = containerId,
            Grantable = true,
            Tags = new[] { ActionTag.Offensive },
        }).IsOk);
        return actionId;
    }

    [Fact]
    public void Preview_returns_the_same_validation_that_Set_then_enforces_and_Preview_writes_nothing()
    {
        SeedHeldAction("skl.ember");
        var ids = new[] { "skl.ember" };

        var preview = _service.Preview(PlayerId, ids);
        Assert.True(preview.Ok);
        Assert.Null(_store.GetLoadout(new OwnerScope(OwnerKind.Player, PlayerId.ToString())));

        var set = _service.Set(PlayerId, ids);
        Assert.Equal(preview, set);
        Assert.Equal(ids, _store.GetLoadout(new OwnerScope(OwnerKind.Player, PlayerId.ToString())));
    }

    [Fact]
    public void Preview_and_Set_both_admit_a_real_aura_id_that_is_never_an_ActionRow()
    {
        // aura-skill T18c regression, same fixture LoadoutEndpointsTests.cs uses: "Might" is a real,
        // shipped AuraContentCatalog id that store.GetAction alone would never find.
        var ids = new[] { "Might" };

        Assert.True(_service.Preview(PlayerId, ids).Ok);
        Assert.True(_service.Set(PlayerId, ids).Ok);
    }

    [Fact]
    public void Preview_and_Set_refuse_the_same_way_for_an_unheld_action_and_Set_writes_nothing()
    {
        var ids = new[] { "not-a-real-action" };

        var preview = _service.Preview(PlayerId, ids);
        var set = _service.Set(PlayerId, ids);

        Assert.Equal(preview, set);
        Assert.False(set.Ok);
        Assert.Equal(LoadoutRejectionReason.ActionNotHeld, set.Reason);
        Assert.Equal("not-a-real-action", set.ActionId);
        Assert.Null(_store.GetLoadout(new OwnerScope(OwnerKind.Player, PlayerId.ToString())));
    }
}
