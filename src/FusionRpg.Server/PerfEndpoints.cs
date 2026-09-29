using System.Text.Json;

namespace FusionRpg.Server;

/// <summary>In-memory ring of injector PerfProbe windows (~5s each) — perf-probe-plan.md §1.4.</summary>
public sealed class PerfWindowBuffer
{
    // Structural (tunables-ssot.md T2) — ring-buffer size, not balance.
    public const int Cap = 240; // ~20 minutes of 5s windows

    readonly object _gate = new();
    readonly Queue<JsonElement> _items = new();

    public void Add(JsonElement window)
    {
        lock (_gate)
        {
            _items.Enqueue(window.Clone());
            while (_items.Count > Cap)
                _items.Dequeue();
        }
    }

    /// <summary>Newest-last, capped at <paramref name="limit"/>.</summary>
    public List<JsonElement> Recent(int limit)
    {
        lock (_gate)
        {
            var skip = Math.Max(0, _items.Count - limit);
            var list = new List<JsonElement>(Math.Min(limit, _items.Count));
            var i = 0;
            foreach (var item in _items)
            {
                if (i++ < skip) continue;
                list.Add(item);
            }
            return list;
        }
    }

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }
}

public static class PerfEndpoints
{
    public static void MapPerf(this WebApplication app)
    {
        app.MapPost("/api/perf", (JsonElement body, PerfWindowBuffer buf) =>
        {
            if (body.ValueKind != JsonValueKind.Object)
                return Results.BadRequest(new { error = "object body required" });
            buf.Add(body);
            return Results.Ok(new { ok = true, count = buf.Count });
        });

        app.MapGet("/api/perf/recent", (PerfWindowBuffer buf, int? limit) =>
            Results.Ok(new { items = buf.Recent(Math.Clamp(limit ?? 24, 1, PerfWindowBuffer.Cap)) }));
    }
}
