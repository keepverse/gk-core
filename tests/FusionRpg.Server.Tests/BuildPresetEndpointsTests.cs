using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FusionRpg.Core.BuildPresets;
using FusionRpg.Core.Saves;
using FusionRpg.Data;
using FusionRpg.Data.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>BP1.12 — the four build-preset routes over a real in-process host and in-memory store.</summary>
[Trait("VerificationId", "server.build-preset")]
public sealed class BuildPresetEndpointsTests : IAsyncLifetime
{
    DataTestStore _testStore = null!;
    RpgStore _store = null!;
    WebApplication _app = null!;
    HttpClient _http = null!;
    long _playerId;
    EmpireRef _owner;

    public async Task InitializeAsync()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _playerId = _store.GetCurrentPlayerId();
        _owner = EmpireScopeRequests.HumanOwnerOf(_store, _playerId);
        BuildPresetTuningHub.Configure(new BuildPresetTuning(1, 1, SoftMaxBuildPresets: 32));

        var port = GetFreeTcpPort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_store);
        builder.WebHost.UseUrls(baseUrl);
        _app = builder.Build();
        _app.UseDeveloperExceptionPage();
        _app.MapBuildPresets();
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        _testStore.Dispose();
    }

    [Fact]
    public async Task Post_get_put_and_delete_round_trip_the_validated_library()
    {
        var created = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-http",
            name = "Might lean",
            pieces = new[] { Piece("skills", "player", 0, "Might") },
        });
        await AssertSuccessAsync(created);
        using (var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync()))
        {
            Assert.Equal(1, body.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal("present", body.RootElement.GetProperty("pieces")[0].GetProperty("state").GetString());
        }

        var replaced = await _http.PutAsJsonAsync("/api/build-presets/bp-http", new
        {
            playerId = _playerId,
            name = "Fortitude lean",
            pieces = new[] { Piece("skills", "player", 0, "Fortitude") },
        });
        await AssertSuccessAsync(replaced);
        using (var body = JsonDocument.Parse(await replaced.Content.ReadAsStringAsync()))
        {
            Assert.Equal("Fortitude lean", body.RootElement.GetProperty("name").GetString());
            Assert.Equal(2, body.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal("Fortitude", body.RootElement.GetProperty("pieces")[0].GetProperty("refId").GetString());
        }

        var list = await _http.GetAsync($"/api/build-presets/{_playerId}");
        await AssertSuccessAsync(list);
        using (var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync()))
        {
            Assert.Equal(_playerId, body.RootElement.GetProperty("playerId").GetInt64());
            var preset = Assert.Single(body.RootElement.GetProperty("presets").EnumerateArray());
            Assert.Equal("bp-http", preset.GetProperty("presetId").GetString());
            Assert.Equal(2, preset.GetProperty("revision").GetInt64());
            // "list with validated pieces": the LIST route validates exactly as the single-preset
            // route does, so a listed piece carries its own state -- a plain header list would
            // return an empty/absent `pieces` and pass every assertion above.
            var listed = Assert.Single(preset.GetProperty("pieces").EnumerateArray());
            Assert.Equal("skills", listed.GetProperty("kind").GetString());
            Assert.Equal("Fortitude", listed.GetProperty("refId").GetString());
            Assert.Equal("present", listed.GetProperty("state").GetString());
        }

        var deleted = await _http.DeleteAsync(
            $"/api/build-presets/bp-http?playerId={_playerId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty(_store.ListBuildPresets(_owner));
        Assert.Empty(_store.GetBuildPresetPieceRows("bp-http", _owner));
    }

    [Fact]
    public async Task PlayerId_is_the_save_id_on_every_wire_operation()
    {
        var other = _store.CreatePlayer("Other save");
        var otherId = other.Id;

        var created = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = otherId,
            presetId = "bp-other-save",
            name = "Other save",
            pieces = new[] { Piece("skills", "player", 0, "Might") },
        });
        await AssertSuccessAsync(created);

        var first = await _http.GetAsync($"/api/build-presets/{_playerId}");
        var second = await _http.GetAsync($"/api/build-presets/{otherId}");
        await AssertSuccessAsync(first);
        await AssertSuccessAsync(second);
        using var firstBody = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        using var secondBody = JsonDocument.Parse(await second.Content.ReadAsStringAsync());
        Assert.Empty(firstBody.RootElement.GetProperty("presets").EnumerateArray());
        Assert.Equal("bp-other-save",
            Assert.Single(secondBody.RootElement.GetProperty("presets").EnumerateArray())
                .GetProperty("presetId").GetString());
    }

    public static TheoryData<string, PieceBody[], string> ShapeRefusals()
    {
        var patron = "patron.test";
        return new TheoryData<string, PieceBody[], string>
        {
            { "Empty", Array.Empty<PieceBody>(), "build-preset.empty" },
            {
                "Patrons",
                new[] { new PieceBody("patron", "", 0, patron), new PieceBody("patron", "", 1, patron) },
                "build-preset.patron.multiple"
            },
            {
                "Field",
                new[] { new PieceBody("patron", "", 0, patron), new PieceBody("field", "", 1, patron) },
                "build-preset.field.shape"
            },
            {
                "Outside",
                new[] { new PieceBody("patron", "", 0, patron), new PieceBody("field", "", 0, "other") },
                "build-preset.patron.outside-field"
            },
            {
                "Scope",
                new[] { new PieceBody("aptitudes", "unknown:id", 0, "apt") },
                "build-preset.aptitudes.scope"
            },
            {
                "Skills",
                new[] { new PieceBody("skills", "player", 1, "Might") },
                "build-preset.skills.shape"
            },
            { "Name", new[] { new PieceBody("skills", "player", 0, "Might") }, "build-preset.name.missing" },
        };
    }

    [Theory]
    [MemberData(nameof(ShapeRefusals))]
    public async Task Every_shape_refusal_reaches_the_client_and_writes_nothing(
        string id,
        PieceBody[] pieces,
        string expectedReason)
    {
        var response = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-" + id,
            name = id == "Name" ? " " : id,
            pieces,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedReason, await ReasonAsync(response));
        Assert.Empty(_store.ListBuildPresets(_owner));
    }

    [Fact]
    public async Task A_put_refusal_preserves_the_existing_header_and_pieces()
    {
        var created = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-put-refusal",
            name = "Before",
            pieces = new[] { Piece("skills", "player", 0, "Might") },
        });
        await AssertSuccessAsync(created);

        var refused = await _http.PutAsJsonAsync("/api/build-presets/bp-put-refusal", new
        {
            playerId = _playerId,
            name = "After",
            pieces = new[] { Piece("skills", "player", 1, "Fortitude") },
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("build-preset.skills.shape", await ReasonAsync(refused));
        var row = Assert.IsType<RpgBuildPresetRow>(_store.GetBuildPreset("bp-put-refusal", _owner));
        Assert.Equal("Before", row.Name);
        Assert.Equal(1, row.Revision);
        Assert.Equal("Might", Assert.Single(_store.GetBuildPresetPieceRows("bp-put-refusal", _owner)).RefId);
    }

    [Fact]
    public async Task Another_save_can_neither_replace_nor_delete_the_preset()
    {
        var created = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-owned-http",
            name = "Owned",
            pieces = new[] { Piece("skills", "player", 0, "Might") },
        });
        await AssertSuccessAsync(created);
        var other = EmpireScopeRequests.HumanOwnerOf(_store, _store.CreatePlayer("Other").Id);

        var put = await _http.PutAsJsonAsync("/api/build-presets/bp-owned-http", new
        {
            playerId = other.Save.Value,
            name = "Stolen",
            pieces = new[] { Piece("skills", "player", 0, "Fortitude") },
        });
        Assert.Equal(HttpStatusCode.Conflict, put.StatusCode);
        Assert.Equal("build-preset.owner.mismatch", await ReasonAsync(put));

        var delete = await _http.DeleteAsync(
            $"/api/build-presets/bp-owned-http?playerId={other.Save.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        Assert.Equal("build-preset.notFound", await ReasonAsync(delete));
        Assert.Equal("Owned", _store.GetBuildPreset("bp-owned-http", _owner)!.Name);
        Assert.Single(_store.GetBuildPresetPieceRows("bp-owned-http", _owner));
        Assert.Null(_store.GetBuildPreset("bp-owned-http", other));
    }

    [Fact]
    public async Task An_unknown_kind_is_a_client_refusal_and_not_a_stored_row()
    {
        var response = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-future-kind",
            name = "Future",
            pieces = new[] { new PieceBody("sixth", "", 0, "future") },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("build-preset.kind.unknown", await ReasonAsync(response));
        Assert.Empty(_store.ListBuildPresets(_owner));
    }

    [Fact]
    public async Task Create_at_the_loaded_soft_max_returns_the_code_and_writes_nothing()
    {
        // The hub is process-global, so the lowered cap is restored: leaving softMaxBuildPresets at 1
        // would silently refuse creates in every later test that shares this assembly's process.
        var restore = BuildPresetTuningHub.Tuning;
        try
        {
            BuildPresetTuningHub.Configure(new BuildPresetTuning(1, 1, SoftMaxBuildPresets: 1));
            var first = await _http.PostAsJsonAsync("/api/build-presets", new
            {
                playerId = _playerId,
                presetId = "bp-soft-one",
                name = "One",
                pieces = new[] { Piece("skills", "player", 0, "Might") },
            });
            await AssertSuccessAsync(first);

            var second = await _http.PostAsJsonAsync("/api/build-presets", new
            {
                playerId = _playerId,
                presetId = "bp-soft-two",
                name = "Two",
                pieces = new[] { Piece("skills", "player", 0, "Fortitude") },
            });

            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.Equal("build-preset.softMax", await ReasonAsync(second));
            Assert.Equal("bp-soft-one", Assert.Single(_store.ListBuildPresets(_owner)).PresetId);
        }
        finally
        {
            BuildPresetTuningHub.Configure(restore);
        }
    }

    [Fact]
    public async Task The_list_route_revalidates_so_a_stale_reference_reads_missing_with_its_reason()
    {
        // A shape-legal save is not a validity promise (spec-preset-store.md "Save-time rules"): the
        // skills piece names an action that does not exist, so the LIST route must show the hole and
        // the row, rather than a quietly shorter preset.
        var created = await _http.PostAsJsonAsync("/api/build-presets", new
        {
            playerId = _playerId,
            presetId = "bp-stale",
            name = "Stale",
            pieces = new[] { Piece("skills", "player", 0, "skill.does-not-exist") },
        });
        await AssertSuccessAsync(created);

        var list = await _http.GetAsync($"/api/build-presets/{_playerId}");
        await AssertSuccessAsync(list);
        using var body = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var listed = Assert.Single(
            Assert.Single(body.RootElement.GetProperty("presets").EnumerateArray())
                .GetProperty("pieces").EnumerateArray());
        Assert.Equal("missing", listed.GetProperty("state").GetString());
        Assert.Equal("build-preset.piece.missing:skills", listed.GetProperty("reason").GetString());
        Assert.Equal("skill.does-not-exist", listed.GetProperty("refId").GetString());
    }

    [Fact]
    public void Production_composition_maps_the_build_preset_routes()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "FusionRpg.Server", "Program.cs"));
        Assert.Contains("app.MapBuildPresets();", program, StringComparison.Ordinal);
    }

    static PieceBody Piece(string kind, string target, long ordinal, string reference) =>
        new(kind, target, ordinal, reference);

    static async Task AssertSuccessAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
    }

    static async Task<string?> ReasonAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("reason").GetString();
    }

    static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "FusionRpg.Injector")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repo root");
    }

    public sealed record PieceBody(string Kind, string TargetRef, long Ordinal, string RefId);
}
