using System.Text.Json;

namespace FusionRpg.Core.Items.Drops;

public sealed class DropRateFloorTuningRejection : Exception
{
    public DropRateFloorTuningRejection(string message) : base(message) { }
}

/// <summary>
/// `gk-core/data/tuning/drop-rate-floor.v1.json`, parsed. Pure — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."), the same shape <see cref="DropVolumeTuning"/> already
/// established.
///
/// <para>The rarest any drop-table entry may ever be configured to be, expressed finely enough to name
/// 0.0001% (`MinRatePerMillion: 1`). A DROP-TABLE-ENTRY concept — never touches the rarity ladder
/// (`item-rarity.v1.json`'s `dropWeightPer100k`) or `bands.v1.json`'s frozen registry. See
/// `spec-rate-floor.md`.</para>
/// </summary>
public readonly record struct DropRateFloorTuning(long MinRatePerMillion)
{
    public static DropRateFloorTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DropRateFloorTuningRejection("drop-rate-floor tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new DropRateFloorTuningRejection($"drop-rate-floor tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var minRatePerMillion = Long(root, "minRatePerMillion");
            var parsed = new DropRateFloorTuning(minRatePerMillion);
            Validate(parsed);
            return parsed;
        }
    }

    /// <summary>Refuses a document that is self-inconsistent — the same posture
    /// <see cref="DropVolumeTuning.Validate"/> already established for its own file.</summary>
    public static void Validate(DropRateFloorTuning t)
    {
        if (t.MinRatePerMillion <= 0)
            throw new DropRateFloorTuningRejection(
                $"drop-rate-floor tuning: minRatePerMillion {t.MinRatePerMillion} must be positive — a " +
                "zero-or-negative floor refuses everything, which is not a floor");
        if (t.MinRatePerMillion >= 1_000_000)
            throw new DropRateFloorTuningRejection(
                $"drop-rate-floor tuning: minRatePerMillion {t.MinRatePerMillion} must be below " +
                "1,000,000 — a floor at or above the whole scale would refuse every possible entry");
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new DropRateFloorTuningRejection($"drop-rate-floor tuning: missing or non-integer '{key}'");
        return v;
    }
}
