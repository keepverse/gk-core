using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms.Power;

public sealed class PowerPredicateTuningRejection : Exception
{
    public PowerPredicateTuningRejection(string message) : base(message) { }
}

/// <param name="DiscountFloorMilli">`P0.3` (spec-power-vector.md): the floor on a predicate's
/// four-factor discount — <c>predicateFrequency = max(floorMilli, chain)</c>. "The chain measures
/// the AVERAGE case. The price has to hold against the BEST case" — a floor bounds how far a combo
/// build's discount may fall, never how large the effect itself may be (PS-8 exempt bounded ratio,
/// and this comment is that exemption). Band 400-500 per the owner's own call ("2 or 2.5 cheaper");
/// 400 is the shipped default, matching the spec's own worked numbers exactly.</param>
public sealed record PowerPredicateTuning(int DiscountFloorMilli);

/// <summary>
/// Unlike <c>FusionRpg.Core.Power.PowerTuningHub</c> (which throws until a host explicitly
/// configures it), this hub ships a real, defensible default so every EXISTING caller of
/// <see cref="CostFunction.Conditionality"/> keeps working unconfigured — this module has no host
/// wiring yet (the same "zero production callers" shape as several other pieces built this session),
/// and a hard-fail-until-configured hub would break the whole existing test suite for a value that
/// already has a sane, spec-named default.
/// </summary>
public static class PowerPredicateTuningHub
{
    public static readonly PowerPredicateTuning Default = new(DiscountFloorMilli: 400);

    static PowerPredicateTuning? _tuning;

    public static void Configure(PowerPredicateTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    /// <summary>Explicit test/host reset — <see cref="Configure"/> has no matching "clear," and a
    /// test that configures a non-default floor must not leak it into the next one.</summary>
    public static void Reset() => _tuning = null;

    public static PowerPredicateTuning Current => _tuning ?? Default;
}

/// <summary>Pure parser for `data/tuning/power-predicate.v{n}.json` (tunables-ssot.md §7.2 — no file
/// I/O here).</summary>
public static class PowerPredicateTuningLoader
{
    public static PowerPredicateTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PowerPredicateTuningRejection("power predicate tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new PowerPredicateTuningRejection($"power predicate tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("discountFloorMilli", out var el)
                || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var floor))
                throw new PowerPredicateTuningRejection("power predicate tuning: missing or non-integer 'discountFloorMilli'");

            if (floor is <= 0 or > 1000)
                throw new PowerPredicateTuningRejection(
                    $"power predicate tuning: discountFloorMilli {floor} must be in (0, 1000] " +
                    "(PS-8 -- a zero floor is an uncapped discount, which is the exact defect the floor exists to prevent)");

            return new PowerPredicateTuning(floor);
        }
    }
}
