using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Data;
using Microsoft.AspNetCore.SignalR;
using FusionRpg.Core.Time;

namespace FusionRpg.Server;

public sealed class SimService
{
    private readonly RpgStore _store;
    private readonly EventIngest _ingest;
    private readonly ILogger<SimService> _log;
    private readonly object _gate = new();

    public SimEngine Engine { get; } = new();
    public bool Helloed { get; private set; }

    public SimService(RpgStore store, EventIngest ingest, ILogger<SimService> log)
    {
        _store = store;
        _ingest = ingest;
        _log = log;
    }

    public IResult? Guard()
    {
        if (_store.LiveInjector)
            return Results.Json(new { error = "live injector connected" }, statusCode: 409);
        return null;
    }

    public Task HelloAsync()
    {
        lock (_gate) Helloed = true;
        _store.Heartbeat(RpgConstants.SourceSim);
        _log.LogInformation("sim hello");
        Publish(new[]
        {
            new EventEnvelope
            {
                T = ServerClock.UtcNowDateTime.ToString("o"),
                Game = RpgConstants.GameId,
                Kind = "injector.hello",
                Payload = new HelloDto { Game = RpgConstants.GameId, Version = "sim" }
            }
        });
        Publish(Engine.CatalogTypes().Events);
        Publish(Engine.CatalogRecipes().Events);
        return Task.CompletedTask;
    }

    public void StopHello()
    {
        lock (_gate) Helloed = false;
    }

    public async Task FullResetAsync()
    {
        await _ingest.FlushPendingAsync().ConfigureAwait(false);
        StopHello();
        Engine.Reset();
        _store.Reset();
        _log.LogInformation("sim/test reset");
    }

    public Task<IResult> RunAsync(Func<StatsConfig, SimResult> act)
    {
        var blocked = Guard();
        if (blocked != null) return Task.FromResult(blocked);
        SimResult result;
        lock (_gate)
            result = act(_store.GetStats());
        if (result.Error != null)
            return Task.FromResult(Results.NotFound(new { error = result.Error }));
        Publish(result.Events);
        return Task.FromResult(Results.Ok(new { skipped = result.Skipped, events = result.Events, matchKey = result.MatchKey ?? Engine.MatchKey }));
    }

    public void Publish(IEnumerable<EventEnvelope> events) => _ingest.EnqueueRange(events);
}
