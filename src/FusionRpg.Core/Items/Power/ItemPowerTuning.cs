using System.Text.Json;

namespace FusionRpg.Core.Items.Power;

/// <summary>Every number a balance pass would touch for the item power reads (spec-item-power-reads.md).</summary>
public readonly record struct ItemPowerTuning(
    int ImplicitShareCapMilli, int? GrantedActionShareCapMilli, bool ShowPowerOnCard,
    int PowerDisplaySigFigs, int PowerDisplayBandPercent);

public sealed class ItemPowerTuningRejection : Exception
{
    public ItemPowerTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser over `gk-core/data/tuning/item-power.v1.json` — no file I/O (tunables-ssot.md §7.2).</summary>
public static class ItemPowerTuningLoader
{
    public static ItemPowerTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ItemPowerTuningRejection("item-power tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ItemPowerTuningRejection($"item-power tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var capMilli = root.GetProperty("implicitShareCapMilli").GetInt32();
            var grantedCap = root.TryGetProperty("grantedActionShareCapMilli", out var g) && g.ValueKind == JsonValueKind.Number
                ? g.GetInt32() : (int?)null;
            var showOnCard = root.GetProperty("showPowerOnCard").GetBoolean();
            var sigFigs = root.GetProperty("powerDisplaySigFigs").GetInt32();
            var bandPercent = root.GetProperty("powerDisplayBandPercent").GetInt32();

            // spec's own rule: the band is DERIVED from ContentValidation.DriftTolerancePercent, never
            // independently chosen. Enforced at load, not just by convention in the seed file.
            if (bandPercent != Effects.Atoms.Power.ContentValidation.DriftTolerancePercent)
                throw new ItemPowerTuningRejection(
                    $"item-power tuning: powerDisplayBandPercent ({bandPercent}) must equal "
                    + $"ContentValidation.DriftTolerancePercent ({Effects.Atoms.Power.ContentValidation.DriftTolerancePercent})");

            return new ItemPowerTuning(capMilli, grantedCap, showOnCard, sigFigs, bandPercent);
        }
    }
}
