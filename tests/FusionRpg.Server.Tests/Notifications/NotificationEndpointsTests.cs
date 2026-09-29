using FusionRpg.Contracts;
using FusionRpg.Core.Notify;
using FusionRpg.Core.Saves;
using FusionRpg.Data.Notifications;
using FusionRpg.Data.Tests;
using FusionRpg.Data;
using FusionRpg.Server.Notifications;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;
using System.Net;
using Xunit;

namespace FusionRpg.Server.Tests.Notifications;

/// <summary>notify-service spec §4, Testing 11 - the catch-up page, history page and state changes.</summary>
[Collection("NotificationHub")]
public class NotificationEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    FakePlayerPush _push = null!;
    long _save1;
    long _save2;

    public async Task InitializeAsync()
    {
        NotificationHubFixture.Configure();
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _save1 = _store.GetCurrentPlayerId();
        _save2 = _store.CreatePlayer("Second").Id;
        _push = new FakePlayerPush();

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.Services.AddSingleton<IPlayerPush>(_push);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapNotifications();
        await _app.StartAsync();

        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        _testStore.Dispose();
    }

    void Append(long saveId, string dedupKey, int worldTurn = 1) =>
        _store.AppendNotificationTurn(
            new[] { new NotificationSaveAppend(new SaveId(saveId), new[] { new NotificationInsert(dedupKey, NotificationHubFixture.TestCategory, "important", "src", NotificationHubFixture.TestMessageKey, "[]", null, "w", worldTurn) }) },
            retainPerCategory: 100, createdUtc: "2026-09-19T00:00:00Z", cursor: null);

    [Fact]
    public async Task An_unknown_save_gets_404_on_every_route()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/notifications/999999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync($"/api/notifications/999999/history?category={NotificationHubFixture.TestCategory}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _http.PostAsJsonAsync("/api/notifications/999999/state", new SetNotificationStateRequest { Seqs = new(), State = "read" })).StatusCode);
    }

    [Fact]
    public async Task An_unregistered_category_on_history_gets_400_category_unknown()
    {
        var resp = await _http.GetAsync($"/api/notifications/{_save1}/history?category=nothing.registered");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.Equal("category.unknown", body!["reason"]);
    }

    [Fact]
    public async Task Since_paging_by_rev_has_no_gap_or_repeat()
    {
        Append(_save1, "a");
        Append(_save1, "b");

        var page1 = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}?limit=1");
        Assert.Single(page1!.Items);
        Assert.True(page1.HasMore);

        var page2 = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}?since={page1.NextSince}");
        Assert.Single(page2!.Items);
        Assert.False(page2.HasMore);
        Assert.NotEqual(page1.Items[0].Seq, page2.Items[0].Seq); // no repeat
        Assert.True(page2.Items[0].Rev > page1.Items[0].Rev);     // no gap: the very next rev
    }

    [Fact]
    public async Task A_state_change_made_by_another_session_appears_after_the_cursor()
    {
        Append(_save1, "c");
        var initial = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}");
        var seq = initial!.Items[0].Seq;
        var sinceCursor = initial.Items[0].Rev;

        // "Another session" dismisses it directly through the store (simulating a second connection).
        var changed = _store.SetNotificationState(new SaveId(_save1), new[] { seq }, "dismissed");
        Assert.Single(changed);

        var afterState = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}?since={sinceCursor}");
        Assert.Single(afterState!.Items);
        Assert.Equal("dismissed", afterState.Items[0].State);
    }

    [Fact]
    public async Task The_state_POST_pushes_NotificationStateChanged_to_that_saves_group_only()
    {
        Append(_save1, "d");
        var page = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}");
        var seq = page!.Items[0].Seq;

        var resp = await _http.PostAsJsonAsync($"/api/notifications/{_save1}/state",
            new SetNotificationStateRequest { Seqs = new List<long> { seq }, State = "read" });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<SetNotificationStateResponseDto>();
        Assert.Single(body!.Changed);

        var push = Assert.Single(_push.Pushes);
        Assert.Equal(new SaveId(_save1), push.SaveId);
        Assert.Equal(NotificationEvents.StateChanged, push.EventName);
    }

    [Fact]
    public async Task History_pages_by_seq_descending()
    {
        Append(_save1, "e1");
        Append(_save1, "e2");

        var page = await _http.GetFromJsonAsync<NotificationPageDto>($"/api/notifications/{_save1}/history?category={NotificationHubFixture.TestCategory}");
        Assert.Equal(2, page!.Items.Count);
        Assert.True(page.Items[0].Seq > page.Items[1].Seq); // newest first
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
