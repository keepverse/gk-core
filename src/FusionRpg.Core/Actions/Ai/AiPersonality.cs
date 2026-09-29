using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §3): one actor's
/// offsets, one per <see cref="PersonalityAxis"/>, always inside the profile's own authored bound for
/// that axis. Indexed by the enum's own declaration order — the draw order IS this order, and the
/// storage order matches so a lookup never re-derives it.
/// </summary>
public readonly record struct AiPersonality(IReadOnlyList<int> OffsetByAxis)
{
    public int OffsetOf(PersonalityAxis axis) => OffsetByAxis[(int)axis];
}

/// <summary>
/// combat-ai `ai-tiers-personality` (module 3, CAI1.9, spec-ai-tiers-personality.md §3): D5's
/// personality draw. A UNIQUE creature's personality is a pure function of its instance id alone —
/// stable for life, identical in every match, in every place, with NOTHING STORED. A GENERAL
/// creature's is a pure function of <c>(match seed, actor key)</c> — reproducible within one match.
///
/// <para>No new RNG type and no new public API on <see cref="SeededRng"/>:
/// <c>DeriveStream(0, name)</c> is exactly <c>0 ^ Fnv1a64(name)</c>, a pure function of the name alone
/// (`SeededRng.cs:9-28`) — calling either factory method twice, from two different constructions, in
/// two different processes, produces the SAME offsets, because nothing here reads mutable state.</para>
/// </summary>
public static class AiPersonalityFactory
{
    public static AiPersonality ForUnique(string instanceId, AiPersonalityBounds bounds) =>
        Draw(SeededRng.DeriveStream(0UL, "ai.personality:" + instanceId), bounds);

    public static AiPersonality ForGeneral(ulong matchSeed, string actorKey, AiPersonalityBounds bounds) =>
        Draw(SeededRng.DeriveStream(matchSeed, "ai.personality:" + actorKey), bounds);

    /// <summary>
    /// The draw-order contract (spec §3), each rule closing a real bug class:
    /// <list type="number">
    /// <item>Axes draw in <see cref="PersonalityAxis"/> DECLARATION order, one value each. Two draws
    /// from one stream are order-dependent, so the order is a contract and the enum is append-only
    /// (module 2 states this).</item>
    /// <item>EVERY axis draws unconditionally, including at bound 0, where <c>NextInt(1)</c> always
    /// returns 0. This costs one cheap call and buys a real property: a balance edit to one axis's
    /// bound never shifts another axis's drawn value.</item>
    /// <item>The stream name embeds the IDENTITY, not the seed — <c>"ai.personality:" + id</c> — so an
    /// extra draw for one actor never shifts another's.</item>
    /// </list>
    /// <c>checked</c> on <c>2 * bound + 1</c>: an absurd bound is a configuration error the loader
    /// range-checks at parse, and the arithmetic stays checked anyway (no silent wrap).
    /// </summary>
    static AiPersonality Draw(SeededRng rng, AiPersonalityBounds bounds)
    {
        var axisCount = Enum.GetValues(typeof(PersonalityAxis)).Length;
        var offsets = new int[axisCount];
        for (var axis = 0; axis < axisCount; axis++)
        {
            var bound = bounds.BoundByAxis[(PersonalityAxis)axis];
            var span = checked(2 * bound + 1); // always drawn, even at bound 0 (span == 1 -> NextInt(1) == 0)
            offsets[axis] = rng.NextInt(span) - bound; // unbiased around 0: SeededRng's own rejection sampling
        }
        return new AiPersonality(offsets);
    }
}
