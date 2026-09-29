using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// status-rail B2/C1 — battle projects status-instance StatMods into
/// <see cref="BattleStatModifierLedger"/> on apply and withdraws on end / death teardown
/// (same seam as <c>BattleRunState</c>; nested BattleRunState is not constructible from tests).
/// </summary>
public class BattleStatusStatModsTests
{
    static (StatusRuntime Rt, BattleStatModifierLedger Ledger) Wired()
    {
        var ledger = new BattleStatModifierLedger();
        var rt = new StatusRuntime(
            StatusCatalogBootstrap.CreateDefault(),
            (_, attackerLess) =>
                attackerLess ? ActorDerivedSnapshot.AttackerLess() : ActorDerivedSnapshot.StubNeutral());

        rt.OnApplied = inst =>
        {
            if (inst.StatMods.Count == 0) return;
            foreach (var mod in StatusStatPayload.ToModifiers(inst))
                ledger.Add(inst.HostPtr, mod.Channel, StatusStatPayload.SourceIdOf(inst), mod);
        };
        rt.OnEnded = inst =>
        {
            if (inst.StatMods.Count == 0) return;
            ledger.RemoveBySource(inst.HostPtr, StatusStatPayload.SourceIdOf(inst));
        };
        return (rt, ledger);
    }

    static StatusApplyOutcome ApplyExpose(StatusRuntime rt, DateTimeOffset now) =>
        rt.Apply(
            new StatusApplyInput(
                "expose",
                HostPtr: "squad:0",
                AttackerPtr: "wave:0",
                GrantId: "g-expose",
                BaseMagnitude: 1,
                BaseDuration: 5000,
                DurationMs: 5000,
                StatMods: new[] { new StatusStatMod("atk", "more", -0.1) }),
            new FixedStatusRng(0.0),
            now);

    [Fact]
    public void Apply_ModifyStat_status_adds_channel_delta_withdraw_on_ClearGrant()
    {
        var (rt, ledger) = Wired();
        var now = DateTimeOffset.UtcNow;
        var outcome = ApplyExpose(rt, now);

        Assert.True(outcome.Applied);
        Assert.Single(ledger.For("squad:0", "atk"));

        rt.ClearGrant("g-expose");
        Assert.Empty(ledger.For("squad:0", "atk"));
    }

    [Fact]
    public void Death_TakeHostInstances_tears_down_ledger_without_OnEnded()
    {
        var (rt, ledger) = Wired();
        var ended = 0;
        var prevEnded = rt.OnEnded;
        rt.OnEnded = inst =>
        {
            ended++;
            prevEnded?.Invoke(inst);
        };

        Assert.True(ApplyExpose(rt, DateTimeOffset.UtcNow).Applied);
        Assert.Single(ledger.For("squad:0", "atk"));

        // Mirror BattleRunState.WithdrawStatusHost — OnEnded must stay silent (VFX death contract).
        foreach (var inst in rt.TakeHostInstances("squad:0"))
        {
            if (inst.StatMods.Count == 0) continue;
            ledger.RemoveBySource(inst.HostPtr, StatusStatPayload.SourceIdOf(inst));
        }

        Assert.Equal(0, ended);
        Assert.Empty(rt.ForHost("squad:0"));
        Assert.Empty(ledger.For("squad:0", "atk"));
    }
}
