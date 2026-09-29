using System.Net;
using System.Net.Http.Json;
using FusionRpg.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// combat-ai `commander-direct-orders` (module 20, CAI4.9, spec-commander-direct-orders.md §3/§6): the
/// order route's hard contract — *"the route sends exactly ONE `CommandDto` through
/// `InjectorCommandSender` and returns without awaiting anything else."*
///
/// <para>The command is asserted through the REAL inbox the injector polls (not a mock), so this proves
/// the actual delivery path, exactly as <c>OverlayLeaveEndpointsTests</c> does for the overlay command.
/// The two failure modes it exists to prevent are a route that fans out more than one command per click
/// (the injector would queue an order twice, and a second offer SUPERSEDES the first — so a double relay
/// is a silent order rewrite) and a route that validates a body it cannot validate.</para>
/// </summary>
[Trait("VerificationId", "server.lawn-order")]
public sealed class LawnOrderEndpointTests : IAsyncLifetime
{
    WebApplication _app = null!;
    HttpClient _http = null!;
    InjectorCommandInbox _inbox = null!;

    public async Task InitializeAsync()
    {
        _inbox = new InjectorCommandInbox();

        var port = GetFreeTcpPort();
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(_inbox);
        builder.Services.AddSingleton<InjectorCommandSender>();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapLawnOrders();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task The_route_sends_exactly_one_command_and_returns()
    {
        var response = await _http.PostAsJsonAsync("/api/lawn/order", new
        {
            matchKey = "match.7",
            actorKey = "ptr.a",
            actionId = "act.skill",
            targetKey = "ptr.b"
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // EXACTLY one — a second would SUPERSEDE the first in the injector's queue.
        var sent = _inbox.Drain();
        var cmd = Assert.Single(sent);
        Assert.Equal(LawnOrderEndpoints.CommandName, cmd.Name);

        // The inbox round-trips a command through JSON (that is how the injector polls it), so the
        // payload arrives as a JsonElement rather than the Dictionary the handler built. Read it the way
        // the injector's own dispatch does instead of asserting the in-process type.
        var payload = Assert.IsType<System.Text.Json.JsonElement>(cmd.Payload);
        Assert.Equal("match.7", payload.GetProperty("matchKey").GetString());
        Assert.Equal("ptr.a", payload.GetProperty("actorKey").GetString());
        Assert.Equal("act.skill", payload.GetProperty("actionId").GetString());
        Assert.Equal("ptr.b", payload.GetProperty("targetKey").GetString());
    }

    /// <summary>An incomplete body is refused HERE, with zero commands on the wire: relaying a
    /// half-formed order would make the injector's admission answer a question nobody asked.</summary>
    [Theory]
    [InlineData(null, "ptr.a", "act.skill")]
    [InlineData("match.7", null, "act.skill")]
    [InlineData("match.7", "ptr.a", null)]
    [InlineData("  ", "ptr.a", "act.skill")]
    public async Task An_incomplete_order_is_refused_and_nothing_is_relayed(string? matchKey, string? actorKey, string? actionId)
    {
        var response = await _http.PostAsJsonAsync("/api/lawn/order", new { matchKey, actorKey, actionId, targetKey = "ptr.b" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_inbox.Drain());
    }

    /// <summary>Two clicks are two orders — the route is not idempotent and must not be. The injector's
    /// queue is where "a second order replaces the first" lives.</summary>
    [Fact]
    public async Task Two_posts_relay_two_commands()
    {
        for (var i = 0; i < 2; i++)
            await _http.PostAsJsonAsync("/api/lawn/order", new { matchKey = "match.7", actorKey = "ptr.a", actionId = "act.skill", targetKey = (string?)null });

        Assert.Equal(2, _inbox.Drain().Count);
    }
}
