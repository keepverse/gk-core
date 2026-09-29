using FusionRpg.Core.Stats;
using FusionRpg.Core.Status;
using Xunit;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// E21 (completeness-audit.md finding A1): <c>StatusStatPayload.ToModifiers</c>/<c>SourceIdOf</c> had
/// zero production callers, so <c>rally</c>/<c>expose</c>/<c>command</c>/<c>shatter</c> created
/// instances, played VFX, and changed no stat.
///
/// <para><b>Read-before-build finding:</b> the injector already has a source-tagged session-mod path —
/// <c>ExecModifyStat</c> (FA1's stat.modify executor) does <c>CheatState.Stats.Upsert(mods)</c> on
/// apply and <c>CheatState.Stats.WithdrawSource("effect", sourceId)</c> on removal, exactly the shape
/// <c>ToModifiers</c>/<c>SourceIdOf</c> already produce with <c>SourceKind = "status"</c>. No new
/// plugin was needed — the fix is two calls in <c>EffectRuntime.OnApplied</c>/<c>OnEnded</c> (Unity-
/// hosted, untestable outside the game process; guarded by
/// <c>Guard.Tests/StatusStatApplierGuardTests</c>) mirroring that existing pattern.</para>
///
/// <para>This test proves the half that <b>is</b> testable outside Unity: <see cref="StatSystem"/>,
/// <see cref="StatContext"/> and <see cref="StatComposer"/> are pure Core, so the real
/// Upsert → Resolve → WithdrawSource → Resolve chain — not a mock of it — is run here end to end.
/// This is the audit's exact ask: not "the parser has tests" but "an inert capability is now
/// demonstrably live".</para>
/// </summary>
public class StatusStatApplierSeamTests
{
    static StatusInstance Rally(string instanceId, string hostPtr, params StatusStatMod[] mods) => new()
    {
        InstanceId = instanceId,
        StatusId = "rally",
        HostPtr = hostPtr,
        StatMods = mods,
    };

    [Fact]
    public void A_live_rally_instance_raises_the_composed_channel_through_the_real_stat_system()
    {
        var sys = new StatSystem();
        var baseline = new EntityBaseline { Atk = 100 };
        var ctx = sys.Contexts.ForZombie("Z1", baseline);

        var before = sys.Resolve(ctx);
        Assert.Equal(100, before.Atk);

        var instance = Rally("inst-e21-1", "Z1", new StatusStatMod(StatChannels.Atk, "more", 0.1));
        sys.Upsert(StatusStatPayload.ToModifiers(instance));

        var during = sys.Resolve(ctx);
        Assert.Equal(110, during.Atk); // 100 * 1.1, exact — the real PhasedComposeStrategy, not a stub
    }

    [Fact]
    public void The_channel_returns_to_baseline_once_the_instance_is_withdrawn()
    {
        // The expiry half: WithdrawSource is what ExecModifyStat calls on remove, keyed by the same
        // SourceIdOf the apply half used — proving a status's contribution can actually be taken back,
        // not just added once and forgotten.
        var sys = new StatSystem();
        var baseline = new EntityBaseline { Atk = 100 };
        var ctx = sys.Contexts.ForZombie("Z1", baseline);

        var instance = Rally("inst-e21-2", "Z1", new StatusStatMod(StatChannels.Atk, "more", 0.1));
        sys.Upsert(StatusStatPayload.ToModifiers(instance));
        Assert.Equal(110, sys.Resolve(ctx).Atk);

        sys.WithdrawSource("status", StatusStatPayload.SourceIdOf(instance));

        Assert.Equal(100, sys.Resolve(ctx).Atk);
    }

    [Fact]
    public void Two_stacks_are_two_withdrawable_contributions_through_the_real_system()
    {
        // The other half of the "instance, not status, is the source id" guarantee — proven live,
        // not just as a property of ToModifiers in isolation (StatusStatPayloadTests already covers
        // that). Two +10% More stacks compound: 100 * 1.1 * 1.1 = 121.
        var sys = new StatSystem();
        var baseline = new EntityBaseline { Atk = 100 };
        var ctx = sys.Contexts.ForZombie("Z1", baseline);

        var a = Rally("inst-e21-a", "Z1", new StatusStatMod(StatChannels.Atk, "more", 0.1));
        var b = Rally("inst-e21-b", "Z1", new StatusStatMod(StatChannels.Atk, "more", 0.1));
        sys.Upsert(StatusStatPayload.ToModifiers(a));
        sys.Upsert(StatusStatPayload.ToModifiers(b));

        Assert.Equal(121, sys.Resolve(ctx).Atk);

        sys.WithdrawSource("status", StatusStatPayload.SourceIdOf(a));

        Assert.Equal(110, sys.Resolve(ctx).Atk); // b alone survives — a's withdraw did not take b's mod
    }

    [Fact]
    public void A_status_on_a_different_host_does_not_leak_into_this_ones_resolve()
    {
        // ApplyOwnerKey = HostPtr (ToModifiers) plus StatApplyScope.Matches (StatSystem.Resolve) is
        // what makes this a per-entity contribution rather than a match-wide one — proven by resolving
        // a host the status was never applied to.
        var sys = new StatSystem();
        var baseline = new EntityBaseline { Atk = 100 };
        var otherCtx = sys.Contexts.ForZombie("Z2", baseline);

        var instance = Rally("inst-e21-other", "Z1", new StatusStatMod(StatChannels.Atk, "more", 0.5));
        sys.Upsert(StatusStatPayload.ToModifiers(instance));

        Assert.Equal(100, sys.Resolve(otherCtx).Atk);
    }
}
