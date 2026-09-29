using FusionRpg.Core.Actions.Seeding;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.2 (spec-encounter-generator.md §2 steps 3-4) — count and weighted draw,
/// on named streams, with the same-species cap. Pure: every stream is caller-supplied, nothing here
/// reads a clock or a store.
/// </summary>
public static class SlotFill
{
    /// <summary>
    /// §2 step 3 — `n = NextInt(min, max)` (inclusive both ends, `ExpeditionResolver.cs`'s own
    /// `min + rng.NextInt(max - min + 1)` shape) plus the rung's count delta. Never refuses here —
    /// <c>n &lt; 1</c> is the caller's own refusal to raise, since only the caller has the slot and
    /// encounter context <see cref="EncounterRefusal"/> names.
    /// </summary>
    public static int Count(SeededRng stream, SlotCountBandTuning band, int delta) =>
        band.Min + stream.NextInt(band.Max - band.Min + 1) + delta;

    /// <summary>
    /// §2 step 4 — draws exactly <paramref name="n"/> candidates, weighted 1000 on-climate /
    /// <paramref name="offClimateMilli"/> off-climate (element weighting only — <paramref
    /// name="candidates"/> is already filtered to the spread set by <see cref="SlotFilter.Candidates"/>,
    /// so every candidate here already qualifies; this weight only biases WHICH of the qualified rows
    /// wins more often). <b>XCOM's same-shape guard without a retry loop:</b> once a species holds
    /// <c>⌈n · sameSpeciesMaxMilli / 1000⌉</c> seats it leaves the option list for the REMAINING draws
    /// — draw-without-replacement past the cap, never redraw-until-different. One named stream per
    /// pick (<c>"{streamName}:pick:{k}"</c>) so an extra draw at one index never shifts another.
    /// </summary>
    public static IReadOnlyList<ConcreteAnchor> Draw(
        IReadOnlyList<ConcreteAnchor> candidates, int n, ElementTypeId? climate, long offClimateMilli,
        long sameSpeciesMaxMilli, ulong seed, string streamName)
    {
        if (candidates is null) throw new ArgumentNullException(nameof(candidates));
        if (candidates.Count == 0) throw new ArgumentException("candidates must be non-empty.", nameof(candidates));
        if (n < 1) throw new ArgumentOutOfRangeException(nameof(n), "n must be >= 1.");

        // ceil(n * sameSpeciesMaxMilli / 1000), long widened before the multiply, divided once.
        var cap = (int)((checked((long)n * sameSpeciesMaxMilli) + 999) / 1000);
        if (cap < 1) cap = 1; // a cap of zero would forbid every species outright -- never the intent of a "max share"

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var picked = new List<ConcreteAnchor>(n);

        for (var k = 0; k < n; k++)
        {
            var options = new List<WeightedOption<ConcreteAnchor>>(candidates.Count);
            foreach (var c in candidates)
            {
                if (counts.GetValueOrDefault(c.SpeciesId) >= cap) continue;
                var weight = climate is { } stated && c.ElementPrimary == stated ? 1000 : (int)offClimateMilli;
                options.Add(new WeightedOption<ConcreteAnchor>(c, weight));
            }

            var pickStream = SeededRng.DeriveStream(seed, $"{streamName}:pick:{k}");
            var rollSeed = unchecked((long)pickStream.NextULong());
            ConcreteAnchor chosen;
            try
            {
                chosen = WeightedChoice.Pick(options, rollSeed, $"{streamName}:pick:{k}");
            }
            catch (NoDrawableWeightedOptionException)
            {
                throw new EncounterFillExhausted(
                    $"'{streamName}': the same-species cap ({cap} of {n}) left no drawable candidate for pick {k} of {n} " +
                    $"— {candidates.Count} candidates were not enough distinct species for this count under this cap.");
            }

            picked.Add(chosen);
            counts[chosen.SpeciesId] = counts.GetValueOrDefault(chosen.SpeciesId) + 1;
        }

        return picked;
    }
}

/// <summary>The same-species cap left nothing to draw — distinct from <see cref="EncounterRefusal"/>
/// (which always names a slot) because this can surface from a bare <see cref="SlotFill.Draw"/> call
/// with no slot in scope; <see cref="Encounter.Build"/> re-wraps it as a slot-named
/// <see cref="EncounterRefusal"/> when it has that context.</summary>
public sealed class EncounterFillExhausted : Exception
{
    public EncounterFillExhausted(string message) : base(message) { }
}
