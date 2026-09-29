using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FusionRpg.E2E.Tests;

[Collection("e2e")]
public class CheatsE2ETests : IAsyncLifetime
{
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient _http;

    public CheatsE2ETests(RpgApiFactory factory)
    {
        _http = factory.CreateClient();
    }

    public async Task InitializeAsync()
    {
        var r = await _http.PostAsJsonAsync("/api/test/reset", new { });
        r.EnsureSuccessStatusCode();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Get_cheats_default_empty()
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.Equal(JsonValueKind.Object, doc.ValueKind);
    }

    [Fact]
    public async Task Put_then_get_round_trip()
    {
        var body = new
        {
            persist = false,
            boardConfigLocked = true,
            entries = new[]
            {
                new { id = "A-APPLY", kind = "toggle", enabled = true, floatValue = 0.0 },
                new { id = "E-ZH", kind = "slider", enabled = true, floatValue = 2.5 }
            }
        };
        var put = await _http.PutAsJsonAsync("/api/cheats", body);
        put.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(got.TryGetProperty("entries", out var entries));
        Assert.True(entries.GetArrayLength() >= 2);
        Assert.True(got.TryGetProperty("boardConfigLocked", out var lockEl) && lockEl.GetBoolean());
    }

    [Fact]
    public async Task Toggle_merges_store()
    {
        await _http.PutAsJsonAsync("/api/cheats", new { entries = Array.Empty<object>() });
        var tog = await _http.PostAsJsonAsync("/api/cheats/toggle", new { id = "P-GOD", enabled = true });
        tog.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(got.TryGetProperty("entries", out var entries));
        var found = false;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.GetProperty("id").GetString() == "P-GOD")
            {
                Assert.True(e.GetProperty("enabled").GetBoolean());
                found = true;
            }
        }
        Assert.True(found);
    }

    [Fact]
    public async Task Mirror_put_does_not_require_injector_command()
    {
        // Mirror is catalog-only — must not overwrite web SSOT entries.
        await _http.PutAsJsonAsync("/api/cheats", new
        {
            entries = new[] { new { id = "P-GOD", kind = "toggle", enabled = true, floatValue = 0.0 } }
        });
        var body = new
        {
            persist = false,
            entries = new[] { new { id = "Z-GOD", kind = "toggle", enabled = true, floatValue = 0.0 } },
            catalog = new
            {
                plants = new[] { new { id = 1, name = "Pea" } },
                zombies = Array.Empty<object>()
            }
        };
        var put = await _http.PutAsJsonAsync("/api/cheats/mirror", body);
        put.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(got.TryGetProperty("entries", out var entries));
        Assert.Contains(entries.EnumerateArray(), e => e.GetProperty("id").GetString() == "P-GOD"
            && e.GetProperty("enabled").GetBoolean());
        Assert.DoesNotContain(entries.EnumerateArray(), e => e.GetProperty("id").GetString() == "Z-GOD");
        Assert.True(got.TryGetProperty("catalog", out var catalog));
        Assert.True(catalog.TryGetProperty("plants", out var plants) && plants.GetArrayLength() >= 1);
    }

    [Fact]
    public async Task Probe_packs_list_and_start()
    {
        var list = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/packs", Json);
        Assert.True(list.TryGetProperty("items", out var items));
        Assert.True(items.GetArrayLength() >= 1);
        var packId = items[0].GetProperty("id").GetString();
        Assert.False(string.IsNullOrEmpty(packId));
        var start = await _http.PostAsJsonAsync("/api/cheats/probe", new { packId });
        start.EnsureSuccessStatusCode();
        var body = await start.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.True(body.TryGetProperty("probeId", out var pid));
        Assert.False(string.IsNullOrEmpty(pid.GetString()));
        var end = await _http.PostAsJsonAsync("/api/cheats/probe/end", new { probeId = pid.GetString(), reason = "test" });
        end.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task SetFloat_and_action_ok()
    {
        var sf = await _http.PostAsJsonAsync("/api/cheats/set-float", new { id = "A-P-HP%", value = 3 });
        sf.EnsureSuccessStatusCode();
        var act = await _http.PostAsJsonAsync("/api/cheats/action", new { action = "reapply" });
        act.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(got.TryGetProperty("entries", out var entries));
        var hp = entries.EnumerateArray().First(e => e.GetProperty("id").GetString() == "A-P-HP%");
        Assert.Equal(3, hp.GetProperty("floatValue").GetDouble());
    }

    static long Rev(JsonElement doc) =>
        doc.TryGetProperty("revision", out var r) && r.TryGetInt64(out var v) ? v : 0;

    static bool HasEntry(JsonElement doc, string id) =>
        doc.TryGetProperty("entries", out var entries)
        && entries.EnumerateArray().Any(e => e.GetProperty("id").GetString() == id);

    [Fact]
    public async Task ClearField_removes_entry_and_bumps_revision()
    {
        await _http.PostAsJsonAsync("/api/cheats/set-float", new { id = "A-P-HP%", value = 3 });
        var before = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        var rev0 = Rev(before);
        Assert.True(HasEntry(before, "A-P-HP%"));

        var clear = await _http.PostAsJsonAsync("/api/cheats/clear-field", new { id = "A-P-HP%" });
        clear.EnsureSuccessStatusCode();
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.False(HasEntry(after, "A-P-HP%"));
        Assert.Equal(rev0 + 1, Rev(after));
    }

    [Fact]
    public async Task GetCheats_migrates_legacy_identity_entries_out()
    {
        var put = await _http.PutAsJsonAsync("/api/cheats", new
        {
            entries = new object[]
            {
                new { id = "A-P-HP%", kind = "slider", enabled = true, floatValue = 1.0 },
                new { id = "A-P-ATK%", kind = "slider", enabled = true, floatValue = 2.0 },
                new { id = "E-ZH", kind = "slider", enabled = true, floatValue = 1.0 },
                new { id = "P-HP", kind = "number", enabled = true, floatValue = -1.0 }
            }
        });
        put.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(HasEntry(got, "A-P-ATK%"));
        Assert.False(HasEntry(got, "A-P-HP%"));
        Assert.False(HasEntry(got, "E-ZH"));
        Assert.False(HasEntry(got, "P-HP"));
    }

    [Fact]
    public async Task Toggle_setFloat_clear_each_increments_revision()
    {
        var empty = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        var r0 = Rev(empty);

        await _http.PostAsJsonAsync("/api/cheats/toggle", new { id = "P-GOD", enabled = true });
        var r1 = Rev(await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json));
        Assert.Equal(r0 + 1, r1);

        await _http.PostAsJsonAsync("/api/cheats/set-float", new { id = "A-P-HP%", value = 2 });
        var r2 = Rev(await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json));
        Assert.Equal(r1 + 1, r2);

        await _http.PostAsJsonAsync("/api/cheats/clear-field", new { id = "A-P-HP%" });
        var r3 = Rev(await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json));
        Assert.Equal(r2 + 1, r3);
    }

    [Fact]
    public async Task ResetGroup_single_revision_bump_and_drops_prefix_only()
    {
        await _http.PutAsJsonAsync("/api/cheats", new
        {
            entries = new object[]
            {
                new { id = "A-P-HP%", kind = "slider", enabled = true, floatValue = 2.0 },
                new { id = "P-GOD", kind = "toggle", enabled = true, floatValue = 0.0 },
                new { id = "E-ZH", kind = "slider", enabled = true, floatValue = 3.0 }
            }
        });
        var before = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        var rev0 = Rev(before);

        var act = await _http.PostAsJsonAsync("/api/cheats/action", new { action = "reset-group", prefix = "A-" });
        act.EnsureSuccessStatusCode();
        var after = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.Equal(rev0 + 1, Rev(after));
        Assert.False(HasEntry(after, "A-P-HP%"));
        Assert.True(HasEntry(after, "P-GOD"));
        Assert.True(HasEntry(after, "E-ZH"));
    }

    [Fact]
    public async Task ResetAll_empties_entries()
    {
        await _http.PostAsJsonAsync("/api/cheats/set-float", new { id = "A-P-HP%", value = 4 });
        await _http.PostAsJsonAsync("/api/cheats/toggle", new { id = "P-GOD", enabled = true });
        var act = await _http.PostAsJsonAsync("/api/cheats/action", new { action = "reset-all" });
        act.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        Assert.True(got.TryGetProperty("entries", out var entries));
        Assert.Equal(0, entries.GetArrayLength());
    }

    [Fact]
    public async Task PutCheats_broadcast_body_matches_stored_revision()
    {
        var put = await _http.PutAsJsonAsync("/api/cheats", new
        {
            revision = 99,
            entries = new[]
            {
                new { id = "A-P-ATK%", kind = "slider", enabled = true, floatValue = 2.5 }
            }
        });
        put.EnsureSuccessStatusCode();
        var got = await _http.GetFromJsonAsync<JsonElement>("/api/cheats", Json);
        // Store bumps client-supplied revision (99 → 100), not stale request body.
        Assert.Equal(100, Rev(got));
        Assert.True(HasEntry(got, "A-P-ATK%"));
        Assert.True(got.TryGetProperty("mods", out var mods));
        Assert.Contains(mods.EnumerateArray(), m => m.GetProperty("id").GetString() == "A-P-ATK%");
    }

    [Fact]
    public async Task Schema_endpoint_lists_known_ids()
    {
        var schema = await _http.GetFromJsonAsync<JsonElement>("/api/cheats/schema", Json);
        Assert.True(schema.TryGetProperty("fields", out var fields));
        Assert.True(fields.GetArrayLength() > 10);
        var ids = fields.EnumerateArray().Select(f => f.GetProperty("id").GetString()).ToHashSet();
        Assert.Contains("A-APPLY", ids);
        Assert.Contains("A-P-HP%", ids);
        Assert.Contains("G-TIMESCALE", ids);
        Assert.Contains("SYS-EMIT-PROOF", ids);
        Assert.Contains("SYS-DAMAGE-FX", ids);
        var apply = fields.EnumerateArray().First(f => f.GetProperty("id").GetString() == "A-APPLY");
        Assert.True(apply.GetProperty("toggleDefault").GetBoolean());
    }
}
