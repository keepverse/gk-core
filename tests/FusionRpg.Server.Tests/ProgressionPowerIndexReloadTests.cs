using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Power;
using FusionRpg.Data;
using FusionRpg.Data.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using FusionRpg.Data.Tests;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Server.Tests;

/// <summary>
/// live-probe Task 25 (`tasks/live-probe-todo.md`, found live 2026-09-16, fixed 2026-09-20): Theta (the
/// player's OWN progression level) feeds every magnitude (<c>k x share^gamma x P(Theta)</c>), but
/// <c>CheatState.ApplyPowerSnapshot</c> is only ever refreshed at session start, on reconnect, or on an
/// explicit <c>power.index.reload</c> command — and nothing sent that command when the player's own
/// level changed (only a species level-up had a correctly-scoped signal, <c>EventIngest.cs</c>'s
/// pre-existing "species" `AptitudesUpdated` branch). A player who levelled mid-session kept a stale
/// Theta until the injector restarted.
///
/// <para>Drives <see cref="EventIngest.BroadcastProgressionAsync"/> directly (made <c>internal</c> for
/// exactly this test) rather than a full combat-kill-shaped event through the store's own XP math —
/// no test exercised this dispatch method at all before this fix, and building a real XP-granting
/// event just to reach a dispatch decision would test the store's XP curve a second time, not this
/// method's own fan-out logic, which is what changed.</para>
/// </summary>
public class ProgressionPowerIndexReloadTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    WebApplication _app = null!;
    string _baseUrl = "";
    EventIngest _ingest = null!;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        var store = _testStore.Store;

        PowerTuningHub.Configure(
            PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoTuningDir(), "power-scale.v2.json"))));

        var port = GetFreeTcpPort();
        _baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(o => o.EnableDetailedErrors = true);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IPowerIndexProvider>(sp =>
            new FusionRpg.Server.Power.ServerPowerIndexProvider(sp.GetRequiredService<RpgStore>(), PowerTuningHub.Tuning));
        builder.Services.AddSingleton<InjectorCommandInbox>();
        builder.Services.AddSingleton<EffectGrantSession>();
        builder.Services.AddSingleton<IHotCompactor>(sp => new HotCompactor(sp.GetRequiredService<RpgStore>()));
        builder.Services.AddSingleton<CompactionWorker>();
        builder.Services.AddSingleton<UniqueActorService>();
        builder.Services.AddSingleton<EventIngest>();
        builder.Services.AddSingleton<FusionRpg.Server.DelveBattleSessionManager>();
        builder.Services.AddSingleton<PlayerConnectionRegistry>(); // RpgHub ctor dependency (NS1.6)
        builder.WebHost.UseUrls(_baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapHub<RpgHub>("/hub/rpg");
        await _app.StartAsync();

        _ingest = _app.Services.GetRequiredService<EventIngest>();
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task A_player_level_change_sends_power_index_reload_to_the_injector()
    {
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var receivedCommand = new TaskCompletionSource<CommandDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receivedProgression = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<CommandDto>("Command", cmd =>
        {
            if (cmd is not null && string.Equals(cmd.Name, "power.index.reload", StringComparison.Ordinal))
                receivedCommand.TrySetResult(cmd);
        });
        hub.On<object>("RpgProgressionUpdated", _ => receivedProgression.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);
        await hub.InvokeAsync("Join", RpgConstants.WebGroup);

        var dirty = new[] { new RpgProgressionDirty(playerId, FusionRpg.Core.Progression.RpgActorKinds.Player, 0, 1) };
        await _ingest.BroadcastProgressionAsync(dirty);

        var gotCommand = await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("power.index.reload", gotCommand.Name);
        var gotProgression = await receivedProgression.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gotProgression, "regression: the pre-existing RpgProgressionUpdated broadcast broke");

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_species_level_change_does_not_send_power_index_reload()
    {
        // Regression against over-broadening: a species level-up is NOT the player's own Theta and
        // must keep using only its existing, correctly-scoped "species" AptitudesUpdated signal.
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var receivedAptitudes = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawPowerIndexReload = false;

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<CommandDto>("Command", cmd =>
        {
            if (cmd is not null && string.Equals(cmd.Name, "power.index.reload", StringComparison.Ordinal))
                sawPowerIndexReload = true;
        });
        hub.On<object>("AptitudesUpdated", _ => receivedAptitudes.TrySetResult(true));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var dirty = new[] { new RpgProgressionDirty(playerId, FusionRpg.Core.Progression.RpgActorKinds.Species, 0, 1) };
        await _ingest.BroadcastProgressionAsync(dirty);

        var gotAptitudes = await receivedAptitudes.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gotAptitudes, "regression: the pre-existing species AptitudesUpdated broadcast broke");

        // Give any (incorrect) power.index.reload send a moment to arrive before asserting its absence.
        await Task.Delay(200);
        Assert.False(sawPowerIndexReload, "a species level-up must not reload the PLAYER's own power index");

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_zomboss_species_level_change_names_the_empire_it_credited()
    {
        // ai-empire-species EP4.15 (trigger T1, spec test 6): the species broadcast is ONE emitter for
        // both empires, so it carries WHICH one moved. Before this field a Zomboss species level-up was
        // indistinguishable on the wire from the human player's.
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var received = new TaskCompletionSource<System.Text.Json.JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<System.Text.Json.JsonElement>("AptitudesUpdated", p => received.TrySetResult(p));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var dirty = new[] { new RpgProgressionDirty(
            playerId, FusionRpg.Core.Progression.RpgActorKinds.Species, 0, 1, LevelUps: null, Empire: "zomboss") };
        await _ingest.BroadcastProgressionAsync(dirty);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("species", payload.GetProperty("scope").GetString());
        Assert.Equal("zomboss", payload.GetProperty("empire").GetString());

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_human_species_level_change_leaves_the_empire_field_null()
    {
        // The field is ADDITIVE: every pre-R1 dirty carries no empire, and the wire must say so rather
        // than inventing the human empire's id (an empty/absent answer is what older consumers expect).
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var received = new TaskCompletionSource<System.Text.Json.JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<System.Text.Json.JsonElement>("AptitudesUpdated", p => received.TrySetResult(p));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var dirty = new[] { new RpgProgressionDirty(
            playerId, FusionRpg.Core.Progression.RpgActorKinds.Species, 0, 1) };
        await _ingest.BroadcastProgressionAsync(dirty);

        var payload = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("species", payload.GetProperty("scope").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, payload.GetProperty("empire").ValueKind);

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_zomboss_commander_level_change_names_his_empire_and_not_the_players_reload()
    {
        // ai-empire-species EP4.18 (trigger T5, R23): Zomboss's commander clock credits a
        // kind='player' row under HIS empire, so a Player-kind dirty is not automatically the human's.
        // His level-up moves his own commander budget and must reach the injector as an empire-named
        // AptitudesUpdated -- never as power.index.reload, which re-reads the HUMAN's Theta.
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var zomboss = FusionRpg.Core.Commanders.EmpireId.Zomboss;
        Assert.NotEqual(_testStore.Store.HumanEmpireOf(playerId).Value, zomboss.Value);

        var receivedAptitudes = new TaskCompletionSource<System.Text.Json.JsonElement>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var sawReload = false;

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<CommandDto>("Command", cmd =>
        {
            if (cmd is not null && string.Equals(cmd.Name, "power.index.reload", StringComparison.Ordinal))
                sawReload = true;
        });
        hub.On<System.Text.Json.JsonElement>("AptitudesUpdated", p => receivedAptitudes.TrySetResult(p));
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var dirty = new[] { new RpgProgressionDirty(
            playerId, FusionRpg.Core.Progression.RpgActorKinds.Player, 0, 1,
            LevelUps: null, Empire: zomboss.Value) };
        await _ingest.BroadcastProgressionAsync(dirty);

        var payload = await receivedAptitudes.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("commander", payload.GetProperty("scope").GetString());
        Assert.Equal(zomboss.Value, payload.GetProperty("empire").GetString());
        await Task.Delay(200);
        Assert.False(sawReload, "a Zomboss level-up must not reload the HUMAN player's own power index");

        await hub.DisposeAsync();
    }

    [Fact]
    public async Task A_human_commander_level_change_still_sends_only_the_players_reload()
    {
        // The regression the other direction: naming the empire must not steal the human's own,
        // correctly-scoped signal (live-probe Task 25).
        var playerId = _testStore.Store.GetCurrentPlayerId();
        var human = _testStore.Store.HumanEmpireOf(playerId);
        var receivedCommand = new TaskCompletionSource<CommandDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawAptitudes = false;

        var hub = new HubConnectionBuilder().WithUrl($"{_baseUrl}/hub/rpg").Build();
        hub.On<CommandDto>("Command", cmd =>
        {
            if (cmd is not null && string.Equals(cmd.Name, "power.index.reload", StringComparison.Ordinal))
                receivedCommand.TrySetResult(cmd);
        });
        hub.On<System.Text.Json.JsonElement>("AptitudesUpdated", _ => sawAptitudes = true);
        await hub.StartAsync();
        await hub.InvokeAsync("Join", RpgConstants.InjectorGroup);

        var dirty = new[] { new RpgProgressionDirty(
            playerId, FusionRpg.Core.Progression.RpgActorKinds.Player, 0, 1,
            LevelUps: null, Empire: human.Value) };
        await _ingest.BroadcastProgressionAsync(dirty);

        var gotCommand = await receivedCommand.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("power.index.reload", gotCommand.Name);
        await Task.Delay(200);
        Assert.False(sawAptitudes, "the human's own level-up is not another empire's commander move");

        await hub.DisposeAsync();
    }

    static string RepoTuningDir() => Path.Combine(FindRepoRoot(), "data", "tuning");

    static int GetFreeTcpPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
