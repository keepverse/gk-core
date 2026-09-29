using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Items.Drops;

/// <summary>An entry checked on its OWN named stream, at a fixed rate, decoupled from its group's
/// weighted draw entirely — mirrors D38's kill-roll shape (`DropVolumeTuning.DropChanceOnKillMilli`:
/// "does anything drop" is its own roll, separate from "which rung"). Adding or removing any other
/// entry from the same table never moves this one's rate, which a plain `Weight` cannot guarantee.
/// See `spec-rate-authoring.md`.</summary>
public sealed record IndependentRateEntry(string RefId, long RatePerMillion);

public static class RateAuthoring
{
    /// <summary>
    /// Checks one independent entry on its own named stream — the recommended default for anything
    /// meant to stay exactly rare regardless of what else is authored into the same table later.
    /// `SeededRng.DeriveStream`/`NextInt` (not `AtomRandom`, which exposes only per-mille) is the same
    /// per-system-stream discipline every other roll in this pipeline already uses
    /// (`Battle/SeededRng.cs`: "an extra roll in one system never shifts another").
    /// </summary>
    public static bool Hit(IndependentRateEntry entry, ulong rollSeed, string streamName)
    {
        var roll = SeededRng.DeriveStream(rollSeed, streamName).NextInt(1_000_000);
        return roll < entry.RatePerMillion;
    }

    /// <summary>
    /// Solves for the `Weight` that lands a GROUP-MEMBER entry at <paramref name="targetRatePerMillion"/>
    /// given the group's CURRENT total weight (excluding the entry being solved for). Correct only
    /// until a sibling's weight changes — a design-time calculator, not a runtime draw mechanism. Use
    /// <see cref="Hit"/> instead for anything that must stay exact regardless of future content.
    /// </summary>
    public static long WeightForRate(long targetRatePerMillion, long otherEntriesTotalWeight, DropRateFloorTuning tuning)
    {
        if (targetRatePerMillion < tuning.MinRatePerMillion)
            throw new ArgumentOutOfRangeException(nameof(targetRatePerMillion),
                targetRatePerMillion,
                $"cannot author below the floor of {tuning.MinRatePerMillion} per million");
        if (targetRatePerMillion >= 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(targetRatePerMillion),
                targetRatePerMillion,
                "a target rate must be a real fraction of the group, below 1,000,000 per million");
        if (otherEntriesTotalWeight < 0)
            throw new ArgumentOutOfRangeException(nameof(otherEntriesTotalWeight),
                otherEntriesTotalWeight, "a group's other total weight cannot be negative");

        // weight / (weight + otherTotal) = target / 1_000_000
        // => weight = target * otherTotal / (1_000_000 - target)
        checked
        {
            return targetRatePerMillion * otherEntriesTotalWeight / (1_000_000L - targetRatePerMillion);
        }
    }
}
