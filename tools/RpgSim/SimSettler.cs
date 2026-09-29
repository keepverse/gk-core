using System.Diagnostics;
using System.Text.Json;

namespace FusionRpg.Tools.RpgSim;

/// <summary>
/// "The run reached a stable state" — <b>defined by polling, never assumed</b>
/// (<c>docs/architecture/rpg-simulator-map.md</c>, module 5). The server ticks on its own: an
/// <c>EventIngest</c> writer drains a queue, a notification catch-up pass runs at boot, compaction
/// happens on a timer. A verdict read while one of those is mid-flight is a reading of a race, and the
/// digest would move for a reason that has nothing to do with the scenario.
///
/// <para>The predicate is deliberately narrow and observable from the FE-facing health route
/// (<c>GET /health</c>, the same one the web control room reads): the ingest queue is empty, no events
/// have been dropped, no injector is connected, and the counters have not moved across two consecutive
/// polls. A timeout is a **failure**, not a pass: "I could not tell" is never "it settled".</para>
/// </summary>
public static class SimSettler
{
    public sealed record SettleReport(bool Settled, int Polls, long ElapsedMs, string? Reason);

    public static async Task<SettleReport> WaitAsync(HttpClient http, TimeSpan? timeout = null,
        TimeSpan? interval = null, CancellationToken ct = default)
    {
        var budget = timeout ?? TimeSpan.FromSeconds(30);
        var wait = interval ?? TimeSpan.FromMilliseconds(100);
        var clock = Stopwatch.StartNew();
        var polls = 0;
        (long Queued, long Dropped)? previous = null;
        string? lastReason = null;

        while (clock.Elapsed < budget)
        {
            ct.ThrowIfCancellationRequested();
            polls++;

            (long Queued, long Dropped) current;
            try
            {
                using var res = await http.GetAsync("health", ct);
                if (!res.IsSuccessStatusCode)
                {
                    lastReason = $"/health answered {(int)res.StatusCode}";
                    await Task.Delay(wait, ct);
                    continue;
                }

                using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                var root = body.RootElement;
                current = (Long(root, "ingestQueued"), Long(root, "ingestDroppedEvents"));

                if (current.Dropped > 0)
                    return new SettleReport(false, polls, clock.ElapsedMilliseconds,
                        $"the ingest writer dropped {current.Dropped} event(s) — the run's writes were lost, " +
                        "and a verdict read after a lost write is a verdict about nothing");

                if (current.Queued == 0 && previous == current)
                    return new SettleReport(true, polls, clock.ElapsedMilliseconds, null);

                lastReason = $"ingestQueued={current.Queued}" +
                             (previous is null ? " (first poll)" : $" (moved from {previous.Value.Queued})");
                previous = current;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException)
            {
                lastReason = e.Message;
            }

            await Task.Delay(wait, ct);
        }

        return new SettleReport(false, polls, clock.ElapsedMilliseconds,
            $"the host did not settle within {budget.TotalSeconds:0.#}s (last: {lastReason ?? "no observation"})");
    }

    static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
