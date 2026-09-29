using System.Collections.Concurrent;
using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Server;

/// <summary>
/// Reliable web→injector command delivery. SignalR group sends are best-effort;
/// the injector polls this inbox over HTTP so cheats work even when server→client
/// SignalR delivery fails (common after reconnect without re-Join).
/// </summary>
public sealed class InjectorCommandInbox
{
    // Structural (tunables-ssot.md T2) — bounded queue memory, not balance.
    const int Cap = 2_000;
    readonly ConcurrentQueue<CommandDto> _q = new();
    int _count;

    public int Count => Volatile.Read(ref _count);

    public void Enqueue(CommandDto cmd)
    {
        if (cmd == null || string.IsNullOrWhiteSpace(cmd.Name)) return;
        if (string.IsNullOrWhiteSpace(cmd.Id))
            cmd.Id = Guid.NewGuid().ToString("N");
        cmd.Payload = MakeDurable(cmd.Payload);
        _q.Enqueue(cmd);
        if (Interlocked.Increment(ref _count) > Cap)
        {
            if (_q.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    public List<CommandDto> Drain(int max = 64)
    {
        var list = new List<CommandDto>(Math.Min(max, 64));
        while (list.Count < max && _q.TryDequeue(out var cmd))
        {
            Interlocked.Decrement(ref _count);
            list.Add(cmd);
        }
        return list;
    }

    /// <summary>JsonElement from Minimal API is request-scoped — clone before queuing.</summary>
    static object? MakeDurable(object? payload)
    {
        if (payload == null) return null;
        try
        {
            string raw;
            if (payload is JsonElement el)
            {
                if (el.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                    return null;
                raw = el.GetRawText();
            }
            else
                raw = JsonSerializer.Serialize(payload);
            return JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch
        {
            return null;
        }
    }
}
