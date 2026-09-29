using System.Text.Json;

namespace FusionRpg.Core.Aura;

public sealed class AuraTuningRejection : Exception
{
    public AuraTuningRejection(string message) : base(message) { }
}

/// <summary>
/// aura-skill T10 (`spec-aura-magnitude.md` §3.4): the declared rung→k mapping, an aura's own level
/// axis. `k(rung)` values are per-mille (`kMilli`), directly consumable by
/// <see cref="Stats.Aptitudes.AptitudeReadFunctions.Magnitude"/> without further scaling.
/// </summary>
public sealed record AuraTuning(IReadOnlyDictionary<int, long> RungMapping, int MaxActiveAuras)
{
    /// <summary>Rung 7 is the floor (`consumption`, a `perTick`-cost structural axis, first appears at
    /// rung 7 in `action-rungs.v1.json`, and every aura carries `perTick` upkeep — spec §3.4). Rung 10
    /// is the ceiling (`action-rungs.v1.json`'s own `cap`; there is no rung 11).</summary>
    public const int MinRung = 7;
    public const int MaxRung = 10;

    public long KMilliFor(int rung)
    {
        if (RungMapping.TryGetValue(rung, out var kMilli)) return kMilli;
        throw new ArgumentOutOfRangeException(nameof(rung), rung,
            $"no declared k(rung) mapping for rung {rung} — the usable span is {MinRung}-{MaxRung}");
    }
}

/// <summary>
/// Host-injected <see cref="AuraTuning"/> (tunables-ssot.md §7.2: Core parses a stream, the host reads
/// the file and calls <see cref="Configure"/> once at startup), same shape as every other tuning hub
/// (e.g. <see cref="Power.PowerTuningHub"/>). aura-skill T18c: the first real Server consumer
/// (`AuraRuntimeEndpoints.cs`'s `MaxActiveAuras`) needed this — until then nothing outside tests ever
/// constructed an <see cref="AuraTuning"/> at all.
/// </summary>
public static class AuraTuningHub
{
    static AuraTuning? _tuning;

    public static void Configure(AuraTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static AuraTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "AuraTuningHub.Configure(...) has not run. Hosts read data/tuning/aura.v{n}.json " +
        "and call Configure at startup — there is no built-in default to fall back to.");
}

/// <summary>
/// Pure parser for `data/tuning/aura.v{n}.json` (tunables-ssot.md §7.2 — no file I/O here, matching
/// `RungTableLoader`'s own established shape exactly). Any rung outside [7, 10] is rejected AT LOAD —
/// never silently accepted and never discovered only later at `KMilliFor` call time.
/// </summary>
public static class AuraTuningLoader
{
    public static AuraTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AuraTuningRejection("aura tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new AuraTuningRejection($"aura tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("rungMapping", out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
                throw new AuraTuningRejection("aura tuning: missing 'rungMapping' object");

            var mapping = new Dictionary<int, long>();
            foreach (var prop in mapEl.EnumerateObject())
            {
                if (!int.TryParse(prop.Name, out var rung))
                    throw new AuraTuningRejection($"aura tuning: rungMapping key '{prop.Name}' is not an integer");

                if (rung < AuraTuning.MinRung || rung > AuraTuning.MaxRung)
                    throw new AuraTuningRejection(
                        $"aura tuning: rungMapping declares rung {rung}, outside the usable span " +
                        $"[{AuraTuning.MinRung}, {AuraTuning.MaxRung}] — no aura can exist below rung " +
                        $"{AuraTuning.MinRung} (the `consumption` upkeep floor) or above rung {AuraTuning.MaxRung} " +
                        "(the loaded rung ladder's own cap)");

                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt64(out var kMilli))
                    throw new AuraTuningRejection($"aura tuning: rungMapping[{rung}] is not an integer");
                if (kMilli <= 0)
                    throw new AuraTuningRejection($"aura tuning: rungMapping[{rung}] must be positive, got {kMilli}");

                mapping[rung] = kMilli;
            }

            if (mapping.Count == 0)
                throw new AuraTuningRejection("aura tuning: rungMapping is empty — an aura program with no usable rung is not valid");

            if (!root.TryGetProperty("maxActiveAuras", out var maxEl) || maxEl.ValueKind != JsonValueKind.Number
                || !maxEl.TryGetInt32(out var maxActive))
                throw new AuraTuningRejection("aura tuning: missing or non-integer 'maxActiveAuras'");
            if (maxActive < 1)
                throw new AuraTuningRejection($"aura tuning: maxActiveAuras must be at least 1, got {maxActive}");

            return new AuraTuning(mapping, maxActive);
        }
    }
}
