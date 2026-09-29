using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

// Same namespace as EffectBagAuditTests/EffectBagTests (the file's folder, not a new `…Tests.Effects`
// namespace — that name is already a resolution path other test files depend on).
namespace FusionRpg.Core.Tests;

/// <summary>
/// action-enrich AE2.1 (spec-lawn-action-base.md section "Instakill refusal"): the lawnmower /
/// board-wipe refusal lives in the overlay-filter grammar — the ONE owner — so a plain-amount grant,
/// which never enters <c>DamagePacketBuilder</c>'s event-field branch, is covered too. One new key,
/// <c>excludeInstakill</c>; absent or false, every existing rider keeps its shipped behaviour.
/// </summary>
public class OverlayFilterInstakillTests
{
    static EffectEventDto Dealt(bool instakillShaped = false) => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        MatchKey = "m1",
        Side = "plant",
        ActorPtr = "0xA",
        TargetPtr = "0xB",
        TypeId = 0,
        TargetTypeId = 0,
        Damage = 20,
        Tick = 1,
        InstakillShaped = instakillShaped,
    };

    static Dictionary<string, object?> Filtered(bool excludeInstakill) => new()
    {
        ["filters"] = new Dictionary<string, object?> { ["excludeInstakill"] = excludeInstakill },
    };

    [Fact]
    public void An_opting_out_grant_does_not_fire_on_an_instakill_shaped_event() =>
        Assert.False(EffectOwnerKey.PassesOverlayFilters(Filtered(excludeInstakill: true), Dealt(instakillShaped: true)));

    [Fact]
    public void The_same_grant_still_fires_on_an_ordinary_hit() =>
        Assert.True(EffectOwnerKey.PassesOverlayFilters(Filtered(excludeInstakill: true), Dealt()));

    /// <summary>Spec test: a plain-amount grant WITHOUT the filter behaves as before on an instakill
    /// event — the refusal is opt-in, never a silent change to every other rider.</summary>
    [Fact]
    public void A_grant_with_the_key_false_is_unchanged_on_an_instakill_event() =>
        Assert.True(EffectOwnerKey.PassesOverlayFilters(Filtered(excludeInstakill: false), Dealt(instakillShaped: true)));

    [Fact]
    public void A_grant_with_no_filters_block_at_all_is_unchanged_on_an_instakill_event() =>
        Assert.True(EffectOwnerKey.PassesOverlayFilters(new Dictionary<string, object?>(), Dealt(instakillShaped: true)));

    /// <summary>The key composes with the existing grammar rather than replacing it: an ordinary
    /// (non-instakill) event is unaffected, so a rider that opted out still fires on every normal hit.</summary>
    [Fact]
    public void The_key_does_not_disturb_the_existing_actorIsKiller_filter() =>
        Assert.True(EffectOwnerKey.PassesOverlayFilters(
            new Dictionary<string, object?>
            {
                ["filters"] = new Dictionary<string, object?> { ["excludeInstakill"] = true, ["actorIsKiller"] = false },
            },
            Dealt()));
}
