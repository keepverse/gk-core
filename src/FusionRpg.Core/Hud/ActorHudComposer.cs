using FusionRpg.Core.Match;

namespace FusionRpg.Core.Hud;

/// <summary>Pure assembly of Hot read inputs into <see cref="ActorHudSnapshot"/>.</summary>
public static class ActorHudComposer
{
    public sealed record ActorHudComposeInput(
        bool IsUniquePlant,
        UniqueBindingPhase? BindingPhase,
        int? LevelBand,
        IReadOnlyList<ActorHudShieldStack>? ShieldStacks,
        long ShieldHp,
        long ShieldMax,
        IReadOnlyList<ActorHudStatusToken> StatusTokens,
        int StatusStripMax,
        bool HpSliverEnabled,
        double? HpSliverRatio = null,
        // E41 (spec-ui-attach-point.md §4): the first producer ActorHudResources.Meters has ever
        // had — declared and serialized (ActorHudWireSerializer.cs) since before this module, filled
        // by nothing. Optional/defaulted so every pre-E41 caller (ActorHudBuilder.Build, until it is
        // updated alongside this) keeps compiling and keeps composing a null Meters exactly as before.
        IReadOnlyList<ActorHudMeter>? Meters = null,
        ActorHudElements? Elements = null);

    public static ActorHudSnapshot Compose(ActorHudComposeInput input)
    {
        var flags = new List<string>();
        if (input.IsUniquePlant)
            flags.Add("unique");

        var bound = input.BindingPhase == UniqueBindingPhase.Bound;
        var role = bound ? "specimen" : "vanilla";
        var tier = input.IsUniquePlant || bound ? ActorHudTier.Unique : ActorHudTier.Normal;

        // E41: Meters is populated independently of the shield/HP-sliver branches below (an atom-
        // authored meter has nothing to do with whether this actor also carries a shield), so a
        // Meters-only actor must still get a Resources block even when neither existing branch fires.
        var hasMeters = input.Meters is { Count: > 0 };

        ActorHudResources? resources = null;
        if (input.ShieldStacks is not null)
        {
            resources = new ActorHudResources(
                new ActorHudShield(input.ShieldHp, input.ShieldMax, input.ShieldStacks),
                input.HpSliverEnabled && input.HpSliverRatio is not null
                    ? new ActorHudHpSliver(input.HpSliverRatio.Value)
                    : null,
                input.Meters);
        }
        else if (input.HpSliverEnabled && input.HpSliverRatio is not null)
        {
            resources = new ActorHudResources(null, new ActorHudHpSliver(input.HpSliverRatio.Value), input.Meters);
        }
        else if (hasMeters)
        {
            resources = new ActorHudResources(null, null, input.Meters);
        }

        var (visible, overflow) = ActorHudLayout.Prioritize(input.StatusTokens, input.StatusStripMax);

        return new ActorHudSnapshot(
            new ActorHudIdentity(tier, role, input.LevelBand, flags),
            resources,
            visible,
            new ActorHudOverflow(overflow),
            input.Elements);
    }
}
