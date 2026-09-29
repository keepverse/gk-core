using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// How badly this empire wants one more of something (spec-ai-commander.md §ValueMap).
///
/// This is what makes value **faction-relative**: a fire vein is worth more to an empire short of
/// fire than to one drowning in it, so the same sector scores differently for Zomboss than for you.
/// Marginal utility, in one interface.
///
/// Everything is per-mille against a neutral 1000 — above means "I want this", below means "I have
/// plenty" — so a need can be read as a multiplier without anybody remembering a scale.
/// </summary>
public interface INeedVector
{
    /// <summary>How much this empire wants what a slot of this kind produces.</summary>
    int ForSlotKind(SlotKind kind);

    /// <summary>How much it wants this element specifically. Null asks about elementless yield.</summary>
    int ForElement(ElementTypeId? element);
}

/// <summary>
/// Wants everything equally (spec-ai-commander.md §ValueMap).
///
/// The stub until <c>sector-development</c> ships stockpiles, and deliberately shaped like the real
/// thing rather than avoided: with this in place the AI gets *smarter* the day needs become real,
/// with no change to any AI code. What is missing is the numbers, not the idea.
/// </summary>
public sealed class UniformNeeds : INeedVector
{
    public static readonly UniformNeeds Instance = new();

    /// <summary>Neutral. Multiplying by this changes nothing, which is the point of a stub.</summary>
    public const int Neutral = 1000;

    public int ForSlotKind(SlotKind kind) => Neutral;

    public int ForElement(ElementTypeId? element) => Neutral;
}
