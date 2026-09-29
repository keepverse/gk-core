using System.Text.Json;

namespace FusionRpg.Core.Net;

public sealed record RpgClientTuning(int QueueCap, int DrainSize, int FlushMs);

public sealed record PerfReporterTuning(double IntervalSeconds);

/// <summary>Injector-side networking/telemetry cadence (tunables-ssot.md T1) — not gameplay
/// balance, grouped by concept. Injector-only: Server never runs RpgClient/PerfReporter.</summary>
public sealed record NetTuning(int SchemaVersion, int Version, RpgClientTuning Client, PerfReporterTuning PerfReporter);

public sealed class NetTuningRejection : Exception
{
    public NetTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class NetTuningLoader
{
    public static NetTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new NetTuningRejection("net tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new NetTuningRejection($"net tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var client = Obj(root, "client");
            var perf = Obj(root, "perfReporter");

            return new NetTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: Int(root, "version"),
                Client: new RpgClientTuning(
                    QueueCap: Int(client, "queueCap"),
                    DrainSize: Int(client, "drainSize"),
                    FlushMs: Int(client, "flushMs")),
                PerfReporter: new PerfReporterTuning(
                    IntervalSeconds: Double(perf, "intervalSeconds")));
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new NetTuningRejection($"net tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new NetTuningRejection($"net tuning: missing or non-integer '{key}'");
        return v;
    }

    static double Double(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new NetTuningRejection($"net tuning: missing or non-number '{key}'");
        return el.GetDouble();
    }
}

/// <summary>Holds one net.v{n}.json load for RpgClient/PerfReporter (tunables-ssot.md §7.2).</summary>
public static class NetPolicy
{
    static NetTuning? _tuning;

    public static void Configure(NetTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static NetTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "NetPolicy.Configure(...) has not run. RpgClient/PerfReporter read data/tuning/net.v{n}.json " +
        "(tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
