using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace FusionRpg.E2E.Tests;

/// <summary>
/// live-probe Task 24 (`tasks/live-probe-todo.md`, found live 2026-09-16, fixed 2026-09-20): after
/// <c>PUT /api/players/current</c> the server correctly attributed events to the new player, but the
/// injector's own <c>CheatState.CurrentPlayerId</c> — set only by <c>RpgClient.RefreshPowerIndexAsync</c>
/// (session start, reconnect, or an explicit <c>power.index.reload</c> command) — never moved, so
/// every measurement after a player switch was silently attributed to the boot-time player. Fixed by
/// sending a real <c>power.index.reload</c> command to the injector group on every successful switch
/// (<c>Program.cs</c>'s <c>/api/players/current</c> PUT handler).
/// </summary>
[Collection("e2e")]
public class PlayerSwitchPowerIndexReloadTests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly RpgApiFactory _factory;
    readonly HttpClient _http;

    public PlayerSwitchPowerIndexReloadTests(RpgApiFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Switching_the_current_player_sends_power_index_reload_to_the_injector()
    {
        var created = await _http.PostAsJsonAsync("/api/players", new { name = "Second Player" });
        created.EnsureSuccessStatusCode();
        var second = await created.Content.ReadFromJsonAsync<JsonElement>(Json);
        var secondId = second.GetProperty("id").GetInt64();

        var received = new TaskCompletionSource<CommandDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = _factory.Server.CreateHandler();
        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(_factory.Server.BaseAddress!, "/hub/rpg"), o =>
            {
                o.HttpMessageHandlerFactory = _ => handler;
            })
            .Build();
        hub.On<CommandDto>("Command", cmd =>
        {
            if (cmd is not null && string.Equals(cmd.Name, "power.index.reload", StringComparison.Ordinal))
                received.TrySetResult(cmd);
        });
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var switched = await _http.PutAsJsonAsync("/api/players/current", new { id = secondId });
        switched.EnsureSuccessStatusCode();

        var got = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("power.index.reload", got.Name);

        // Read the result back through the normal path, not the switch response body: the server's
        // own idea of the current player really did move.
        var current = await _http.GetFromJsonAsync<JsonElement>("/api/players/current", Json);
        Assert.Equal(secondId, current.GetProperty("id").GetInt64());

        await hub.DisposeAsync();
    }
}
