namespace FusionRpg.Core.Items;

/// <summary>
/// D29's ilvl->tier ladder (item-ideal.md, `affix-legality` module 8): `1 / 1 / 8 / 18 / 32`, replacing
/// I8's rejected `1/12/25/40/60`. Growth past t5 is carried by `contentScale`, not by a later tier.
/// </summary>
public static class IlvlTierLadder
{
    /// <summary>Minimum ilvl for tiers 1..5, index 0 = t1.</summary>
    public static readonly IReadOnlyList<int> MinIlvlByTier = new[] { 1, 1, 8, 18, 32 };

    public const int MinTier = 1;
    public const int MaxTier = 5;

    /// <summary>The highest tier reachable at a given ilvl — never above <see cref="MaxTier"/>.</summary>
    public static int MaxTierAt(int ilvl)
    {
        var reached = MinTier;
        for (var t = MinTier; t <= MaxTier; t++)
            if (ilvl >= MinIlvlByTier[t - 1]) reached = t;
        return reached;
    }

    /// <summary>
    /// I12's collapsing envelope (the ruled window rule — NOT I8's rejected sliding window, which
    /// would drop t1 out of the window at ilvl 40+). The envelope only ever shrinks toward the rung's
    /// own band as ilvl grows; it never excludes the bottom.
    /// </summary>
    public static (int MinTier, int MaxTier) Envelope(int bandMinTier, int bandMaxTier, int ilvl)
    {
        var maxTier = Math.Min(bandMaxTier, MaxTierAt(ilvl));
        var minTier = Math.Min(bandMinTier, maxTier);
        return (minTier, maxTier);
    }
}

/// <summary>
/// One I12 behaviour that must survive into this module (spec-affix-legality.md): if the narrowed
/// envelope leaves fewer drawable groups than the roll count asks for, narrow the COUNT and record it
/// — never reject a legal drop from legal content.
/// </summary>
public readonly record struct EnvelopeNarrowResult(int RollCount, bool Narrowed);

public static class EnvelopeNarrowing
{
    public static EnvelopeNarrowResult Apply(int requestedRolls, int drawableGroupsInEnvelope) =>
        requestedRolls > drawableGroupsInEnvelope
            ? new EnvelopeNarrowResult(drawableGroupsInEnvelope, Narrowed: true)
            : new EnvelopeNarrowResult(requestedRolls, Narrowed: false);
}
