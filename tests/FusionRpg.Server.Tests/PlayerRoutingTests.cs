using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Saves;
using FusionRpg.Data.Abstractions;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>player-routing spec §1, Testing - `JoinPlayer`, the per-player group, `IPlayerPush`, and
/// the R-N1 isolation acceptance (a push to save 1 never reaches save 2's connection). Real
/// `HubConnectionBuilder` clients against a live TestServer, matching this project's own
/// `AptitudesInjectorBroadcastTests` house style - not a mocking library this test project does not
/// otherwise depend on.</summary>
public class PlayerRoutingTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    string _baseUrl = "";
    long _player1;
    long _player2;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _player1 = _store.GetCurrentPlayerId();
        _player2 = _store.CreatePlayer("Second").Id;

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>();
        builder.Services.AddSingleton<IPlayerPush, HubPlayerPush>();
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        _testStore.Dispose();
    }

    HubConnection NewClient() => new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();

    [Fact]
    public async Task JoinPlayer_with_unknown_id_returns_false_and_joins_nothing()
    {
        var hub = NewClient();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<object>("Probe", _ => received.TrySetResult(true));
        await hub.StartAsync();

        var ok = await hub.InvokeAsync<bool>("JoinPlayer", 999_999L);
        Assert.False(ok);

        _app.Services.GetRequiredService<IPlayerPush>().Push(new SaveId(999_999L), "Probe", new { });
        await Task.Delay(200);
        Assert.False(received.Task.IsCompleted, "an unknown-id JoinPlayer must not have joined the group it refused");
        await hub.DisposeAsync();
    }

    /// <summary>NS7.1, spec §Testing "Archived row": once `players.archived_utc` exists, `JoinPlayer`
    /// refuses an archived row by the same filter `ListPlayers` uses, and the refused id joins
    /// nothing (a push to it reaches no connection).</summary>
    [Fact]
    public async Task JoinPlayer_refuses_an_archived_row_and_joins_nothing()
    {
        var archived = _store.CreatePlayer("Archived").Id;
        Archive(archived);

        // The same filter the boot catch-up walks (ListPlayers -> archived_utc IS NULL).
        Assert.DoesNotContain(_store.ListPlayers(), p => p.Id == archived);

        var hub = NewClient();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<object>("Probe", _ => received.TrySetResult(true));
        await hub.StartAsync();

        Assert.False(await hub.InvokeAsync<bool>("JoinPlayer", archived));

        _app.Services.GetRequiredService<IPlayerPush>().Push(new SaveId(archived), "Probe", new { });
        await Task.Delay(200);
        Assert.False(received.Task.IsCompleted, "an archived row's JoinPlayer must not have joined the group it refused");
        await hub.DisposeAsync();
    }

    /// <summary>The migration's own write (save-identity step 6), applied directly: this fixture is the
    /// archived-row SHAPE, not a re-test of the migration (that lives in Data.Tests).</summary>
    void Archive(long playerId)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE players SET archived_utc = $t WHERE id = $p;";
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$p", playerId);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task JoinPlayer_moves_the_connection_out_of_its_previous_player_group()
    {
        var hub = NewClient();
        var receivedFor1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedFor2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<object>("For1", _ => receivedFor1.TrySetResult(true));
        hub.On<object>("For2", _ => receivedFor2.TrySetResult(true));
        await hub.StartAsync();

        Assert.True(await hub.InvokeAsync<bool>("JoinPlayer", _player1));
        Assert.True(await hub.InvokeAsync<bool>("JoinPlayer", _player2));

        var push = _app.Services.GetRequiredService<IPlayerPush>();
        push.Push(new SaveId(_player1), "For1", new { });
        push.Push(new SaveId(_player2), "For2", new { });

        var got2 = await receivedFor2.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(got2);
        await Task.Delay(200);
        Assert.False(receivedFor1.Task.IsCompleted, "the connection should have left player 1's group when it joined player 2's");
        await hub.DisposeAsync();
    }

    [Fact]
    public async Task HubPlayerPush_never_reaches_a_connection_joined_only_to_WebGroup()
    {
        var hub = NewClient();
        var received = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.On<object>("NotifyProbe", _ => received.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        _app.Services.GetRequiredService<IPlayerPush>().Push(new SaveId(_player1), "NotifyProbe", new { });
        await Task.Delay(200);
        Assert.False(received.Task.IsCompleted, "a content push must never reach WebGroup");
        await hub.DisposeAsync();
    }

    /// <summary>R-N1 acceptance, re-run end to end by the map's Gate G1 (NS3.7): a push to save 1
    /// never reaches save 2's connection.</summary>
    [Fact]
    public async Task Isolation_a_push_to_save_1_reaches_only_save_1_s_connection()
    {
        var hubA = NewClient();
        var hubB = NewClient();
        var receivedA = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedB = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        hubA.On<object>("NotifyProbe", _ => receivedA.TrySetResult(true));
        hubB.On<object>("NotifyProbe", _ => receivedB.TrySetResult(true));
        await hubA.StartAsync();
        await hubB.StartAsync();

        Assert.True(await hubA.InvokeAsync<bool>("JoinPlayer", _player1));
        Assert.True(await hubB.InvokeAsync<bool>("JoinPlayer", _player2));

        _app.Services.GetRequiredService<IPlayerPush>().Push(new SaveId(_player1), "NotifyProbe", new { text = "hello save 1" });

        var gotA = await receivedA.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gotA, "save 1's own connection never received its push");
        await Task.Delay(200);
        Assert.False(receivedB.Task.IsCompleted, "save 2's connection received a push meant for save 1");

        await hubA.DisposeAsync();
        await hubB.DisposeAsync();
    }

    [Fact]
    public async Task Disconnect_clears_the_registry_entry()
    {
        var hub = NewClient();
        await hub.StartAsync();
        Assert.True(await hub.InvokeAsync<bool>("JoinPlayer", _player1));

        var registry = _app.Services.GetRequiredService<PlayerConnectionRegistry>();
        var connectionId = hub.ConnectionId!;
        Assert.True(registry.TryGet(connectionId, out _));

        await hub.DisposeAsync();
        await Task.Delay(300); // OnDisconnectedAsync runs asynchronously server-side
        Assert.False(registry.TryGet(connectionId, out _), "the registry entry must be gone after disconnect");
    }

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
