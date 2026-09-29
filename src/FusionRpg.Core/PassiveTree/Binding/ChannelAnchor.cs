using FusionRpg.Core.Power;

namespace FusionRpg.Core.PassiveTree.Binding;

/// <summary>
/// `channelAnchorMilli` (spec-tree-binder.md §3.3) — the channel's own pin at Θ=20, over hp's pin,
/// in per-mille: `pin_ch · 1000 / pin_hp`. Derived at bake time from `power-scale.v{n}.json`'s own
/// pins, never authored, so a dial change cannot leave it stale. `P(Θ)` is hp-shaped (pinned at
/// `P(20) = 680`); `combat.power` is atk-shaped, `combat.defense` is defense-shaped, and this ratio
/// is what lets one shared ladder price both.
/// </summary>
public static class ChannelAnchor
{
    public sealed class UnknownChannelPin : Exception
    {
        public UnknownChannelPin(string channelId)
            : base($"no pin published for channel '{channelId}' in power-scale.v{{n}}.json's channels block") { }
    }

    /// <summary>
    /// Which of `power-scale.v{n}.json`'s two published channel pins a `combat.*` channel anchors
    /// against — §3.3's own worked example: `combat.power.fire` uses the ATK anchor (135), because
    /// `combat.power.*` is GameUnits, atk-shaped. Scoped to exactly the two families the spec's own
    /// table names (§4's GameUnits row: `combat.power.*` and `combat.defense.*`); `power-scale.v2.json`
    /// itself publishes only two channel pins today ("atk", "defense"), so there is no third family
    /// to anchor against yet. A channel outside both prefixes REFUSES naming it rather than guessing
    /// which of the two pins it should use — the same "never guess" discipline the rest of this module
    /// applies to a missing tunable.
    /// </summary>
    static string AnchorFamilyOf(string channelId) => channelId switch
    {
        "atk" => "atk",
        "defense" => "defense",
        _ when channelId.StartsWith("combat.power.", StringComparison.Ordinal) => "atk",
        _ when channelId.StartsWith("combat.defense.", StringComparison.Ordinal) => "defense",
        _ => throw new UnknownChannelPin(channelId),
    };

    /// <summary>`round_half_away(pinCh · 1000, pinHp)` — the shipped rounding convention every
    /// per-mille derivation in this codebase carries its own copy of (`PowerLadder`, `ChannelLadder`
    /// both do). Verified against the spec's own worked values: atk (92) over hp (680) at 92*1000/680
    /// = 135.29... -> 135; defense (22) gives 32.35... -> 32.</summary>
    public static long ForChannel(string channelId, PowerTuning tuning)
    {
        var family = AnchorFamilyOf(channelId);
        if (!tuning.ChannelsOrEmpty.TryGetValue(family, out var channel))
            throw new UnknownChannelPin(channelId);
        return RoundHalfAwayFromZero(channel.PinValue * 1000, tuning.Curve.PinValue);
    }

    static long RoundHalfAwayFromZero(long numerator, long denominator)
    {
        var q = numerator / denominator;
        var r = numerator % denominator;
        if (r == 0) return q;
        var twiceR = checked(Math.Abs(r) * 2);
        return numerator >= 0
            ? (twiceR >= denominator ? q + 1 : q)
            : (-twiceR >= denominator ? q - 1 : q);
    }
}
