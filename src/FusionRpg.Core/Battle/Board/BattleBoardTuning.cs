using System.Text.Json;

namespace FusionRpg.Core.Battle.Board;

/// <summary>
/// A10 `battle-board` (spec-battle-board.md section 1) — the bounded interval a normal encounter's
/// seeded board size is drawn from. Deliberately its own file rather than a new field on
/// <see cref="SiegeTuning"/>: siege boards are sized from `DistrictLayout`, never from this roll, so
/// the two tunables would otherwise sit unread by each other's own consumer.
/// </summary>
public sealed record BattleBoardTuning(int SchemaVersion, int Version, int MinSide, int MaxSide);

public sealed class BattleBoardTuningRejection : Exception
{
    public BattleBoardTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class BattleBoardTuningLoader
{
    public static BattleBoardTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new BattleBoardTuningRejection("battle-board tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new BattleBoardTuningRejection($"battle-board tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var size = Obj(root, "size");

            var minSide = Int(size, "minSide");
            if (minSide <= 0)
                throw new BattleBoardTuningRejection($"battle-board tuning: size.minSide must be > 0; got {minSide}");

            var maxSide = Int(size, "maxSide");
            if (maxSide < minSide)
                throw new BattleBoardTuningRejection(
                    $"battle-board tuning: size.maxSide ({maxSide}) must be >= size.minSide ({minSide})");

            return new BattleBoardTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                MinSide: minSide,
                MaxSide: maxSide);
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new BattleBoardTuningRejection($"battle-board tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new BattleBoardTuningRejection($"battle-board tuning: missing or non-integer '{key}'");
        return v;
    }
}

/// <summary>See <see cref="BattleBoardTuning"/>.</summary>
public static class BattleBoardTuningPolicy
{
    static BattleBoardTuning? _tuning;

    public static void Configure(BattleBoardTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    static BattleBoardTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "BattleBoardTuningPolicy.Configure(...) has not run. BoardGenerator reads " +
        "data/tuning/battle-board.v1.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    public static int MinSide => Tuning.MinSide;
    public static int MaxSide => Tuning.MaxSide;
}
