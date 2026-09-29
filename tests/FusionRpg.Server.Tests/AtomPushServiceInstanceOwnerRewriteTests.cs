using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats;
using FusionRpg.Data;
using Xunit;
using FusionRpg.Data.Tests;

namespace FusionRpg.Server.Tests;

/// <summary>
/// P1.5-L (2026-09-07) — a real, previously-undiscovered defect found LIVE, not by inspection first:
/// equipping a real rolled <c>stat.modify</c> item on a Lawn-bound specimen, through the real
/// <c>POST /api/items/equip</c> endpoint against a real running server + injector, produced no live
/// stat change. The injector's own event log named the exact cause: <c>"ERR debug.effect.grant:
/// instance: forbidden in Hot; bind to entity:{ptr}"</c>.
///
/// <para><b>Root cause, traced to source</b>: <see cref="AtomPushService.Build"/> stamps every
/// <c>UniqueActor</c>-scoped grant with <see cref="FusionRpg.Core.Match.UniqueOwnerBinder.OwnerKeyForDurableGrant"/>,
/// which produces a durable <c>instance:{id}</c> owner key — the ONE key
/// <c>InjectorEffectActionSink</c>/<c>CheatCommandRunner.RunEffectGrant</c> refuses outright on the hot
/// path by design. The rewrite this needs, <c>UniqueOwnerBinder.BindGrant</c> (<c>instance:{id}</c> -&gt;
/// <c>entity:{ptr}</c>), already existed in Core — but its only caller was the OLDER
/// <c>UniqueLoadoutSpec</c> deploy-time <c>loadoutJson</c> path. Nothing rewrote it for THIS compiled
/// push, since the mechanism was built (T6.1, 2026-09-06) — not something introduced by the equip fix,
/// and not specific to equip: every UniqueActor-scoped grant this service has ever produced, at Hello
/// and at every bind/unbind re-push, was refused the same way.</para>
///
/// <para>These tests prove the fix directly against <see cref="AtomPushService"/>, the same level
/// <see cref="AtomPushServicePatronCallbackTests"/> already tests at, rather than only through the live
/// game (which found the bug but cannot regression-test it).</para>
/// </summary>
public class AtomPushServiceInstanceOwnerRewriteTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly AtomPushService _push;
    const string AtomFamily = "atom.instance-owner-rewrite-proof";

    public AtomPushServiceInstanceOwnerRewriteTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;

        Assert.True(_store.UpsertAtom(new AtomRow
        {
            AtomId = AtomRow.DeriveId(AtomFamily, "", 1), KindId = "stat.modify",
            FamilyId = AtomFamily, Variant = "", Tier = 1, Name = "Instance Owner Rewrite Proof",
            ParamsJson = "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":250}",
        }).IsOk);
        Assert.True(_store.UpsertContainer(new ContainerRow
        {
            ContainerId = "item.instance-owner-rewrite-proof", Kind = ContainerKind.Item,
            Atoms = new[] { new ContainerAtomRow(1, AtomRow.DeriveId(AtomFamily, "", 1)) },
        }).IsOk);

        _push = new AtomPushService(_store);
    }

    public void Dispose()
    {
        _testStore.Dispose();
    }

    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, 80_000, 0, 20, 680, 1000, 25000, 250, 1000, 5000, 5000, 25000);

    /// <summary>Real minted <c>effect_instance</c>, matching <c>RolledItemEquipRuntimeTests.MintRolledInstance</c>'s
    /// exact shape -- <see cref="BindingRow.InstanceId"/> names a real row, never an arbitrary string.</summary>
    string MintInstance()
    {
        var container = _store.GetContainer("item.instance-owner-rewrite-proof")!;
        var atoms = _store.ListAtoms().ToDictionary(a => a.AtomId, StringComparer.Ordinal);
        var r = Instantiator.TryInstantiate(container,
            id => atoms.TryGetValue(id, out var a) ? a : null, _store.GetAffix, 1, 20, Tuning, out var inst);
        Assert.True(r.IsOk, r.ToString());
        return _store.SaveInstance(inst!);
    }

    [Fact]
    public void A_UniqueActor_scoped_grant_for_a_bound_specimen_is_rewritten_to_the_live_ptr_not_left_as_instance()
    {
        var specimenId = _store.CreateUniqueActor(1, "plant", typeId: 1).InstanceId;
        const string ptr = "227F64BC240";

        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = MintInstance(),
            OwnerKind = OwnerKind.UniqueActor,
            OwnerKey = specimenId,
            Slot = "armament-primary",
            Source = "test",
        }).IsOk);

        var corr = "corr-" + Guid.NewGuid().ToString("N");
        Assert.True(_store.TryBeginUniqueDeploy(specimenId, corr).Ok);
        var ack = _store.TryAckUniqueSpawn(corr, ptr);
        Assert.True(ack.Ok, ack.Reason);
        Assert.Equal(ptr, ack.Actor!.LastPtr);

        var payload = _push.Build(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Lawn), matchSeed: 1);

        var grant = Assert.Single(payload.Grants,
            g => _store.ListAtoms().Any(a => a.FamilyId == AtomFamily && a.AtomId == g.EffectId));

        // The whole point: never instance:, and specifically rewritten to THIS specimen's real ptr.
        Assert.False(StatApplyScope.IsInstanceOwnerKey(grant.OwnerKey),
            $"grant owner key '{grant.OwnerKey}' was never rewritten off instance: -- exactly the live refusal this test regresses");
        Assert.Equal(EffectOwnerKeys.Entity(ptr), grant.OwnerKey);
    }

    [Fact]
    public void A_UniqueActor_scoped_grant_for_a_specimen_with_no_live_ptr_yet_is_dropped_not_sent_as_instance()
    {
        // Roster phase: never deployed, so LastPtr is genuinely null. Sending an instance: key here is
        // guaranteed to be refused by the hot path -- dropping it is the documented safe direction.
        var specimenId = _store.CreateUniqueActor(1, "plant", typeId: 1).InstanceId;

        Assert.True(_store.Bind(new BindingRow
        {
            InstanceId = MintInstance(),
            OwnerKind = OwnerKind.UniqueActor,
            OwnerKey = specimenId,
            Slot = "armament-primary",
            Source = "test",
        }).IsOk);

        var payload = _push.Build(
            new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Lawn), matchSeed: 1);

        Assert.DoesNotContain(payload.Grants, g =>
            _store.ListAtoms().Any(a => a.FamilyId == AtomFamily && a.AtomId == g.EffectId));
        // The def itself still compiles and travels -- only the grant (which needs a live owner) is held.
        Assert.Contains(payload.Defs, d => _store.ListAtoms().Any(a => a.FamilyId == AtomFamily && a.AtomId == d.EffectId));
    }
}
