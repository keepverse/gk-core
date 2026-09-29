using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using Xunit;

namespace FusionRpg.Core.Tests;

public class EffectGrantSessionRecorderTests
{
    [Fact]
    public void NoteMatchLifecycle_board_start_end_clears_session()
    {
        var s = new EffectGrantSession();
        s.Upsert(new EffectGrantDto { GrantId = "g1", EffectId = "fx.butter_on_hit", OwnerKey = EffectOwnerKeys.Match });
        EffectGrantSessionRecorder.NoteMatchLifecycle(s, "board.start");
        Assert.Equal(0, s.Count);
        Assert.Null(EffectGrantRehydrate.TryBuildApplyCommand(s.Snapshot()));

        s.Upsert(new EffectGrantDto { GrantId = "g2", EffectId = "fx.freeze_on_hit", OwnerKey = EffectOwnerKeys.Match });
        EffectGrantSessionRecorder.NoteMatchLifecycle(s, "board.end");
        Assert.Equal(0, s.Count);
    }

    [Fact]
    public void NoteMatchLifecycle_other_kinds_noop()
    {
        var s = new EffectGrantSession();
        s.Upsert(new EffectGrantDto { GrantId = "g1", EffectId = "fx.butter_on_hit", OwnerKey = EffectOwnerKeys.Match });
        EffectGrantSessionRecorder.NoteMatchLifecycle(s, "injector.hello");
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void ApplyDebugSteps_scenario_like_grant_withdraw_clear()
    {
        var s = new EffectGrantSession();
        EffectGrantSessionRecorder.ApplyDebugSteps(s, new (string, object?)[]
        {
            ("debug.effect.clear", new { }),
            ("debug.effect.grant", new
            {
                grantId = "live-butter",
                effectId = "fx.butter_on_hit",
                ownerKey = "match",
                overlay = new { icd_ms = 0 }
            }),
            ("debug.effect.grant", new
            {
                grantId = "live-freeze",
                effectId = "fx.freeze_on_hit",
                ownerKey = "match"
            }),
            ("debug.effect.withdraw", new { grantId = "live-freeze" })
        });

        var snap = s.Snapshot();
        Assert.Single(snap);
        Assert.Equal("live-butter", snap[0].GrantId);
        Assert.Equal("fx.butter_on_hit", snap[0].EffectId);
    }

    [Fact]
    public void Overlay_roundtrip_via_apply_command_payload()
    {
        var s = new EffectGrantSession();
        EffectGrantSessionRecorder.ApplyDebugCommand(s, "debug.effect.grant", new
        {
            grantId = "ov",
            effectId = "fx.butter_on_hit",
            ownerKey = "match",
            overlay = new Dictionary<string, object?> { ["icd_ms"] = 0, ["chance"] = 1.0 }
        });

        var cmd = EffectGrantRehydrate.TryBuildApplyCommand(s.Snapshot());
        Assert.NotNull(cmd);
        var json = JsonSerializer.Serialize(cmd!.Payload);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("grants", out var grants));
        Assert.Equal(JsonValueKind.Array, grants.ValueKind);
        var g0 = grants[0];
        Assert.Equal("ov", g0.GetProperty("grantId").GetString());
        Assert.True(g0.TryGetProperty("overlay", out var ov));
        Assert.True(ov.TryGetProperty("icd_ms", out _));

        var host = new SimEffectHost();
        var dto = JsonSerializer.Deserialize<EffectGrantDto>(g0.GetRawText(), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        Assert.NotNull(dto);
        // Sim Grant needs typed overlay values — rebuild from Unwrapped numbers
        dto!.Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0, ["chance"] = 1.0 };
        host.Grant(dto);
        Assert.Single(host.Snapshot().Grants);
        Assert.Equal("ov", host.Snapshot().Grants[0].GrantId);
    }

    [Fact]
    public void TryValidateHotGrantId_rejects_empty()
    {
        Assert.False(EffectGrantSessionRecorder.TryValidateHotGrantId("", out var err));
        Assert.Equal("grantId required", err);
        Assert.False(EffectGrantSessionRecorder.TryValidateHotGrantId(null, out _));
        Assert.True(EffectGrantSessionRecorder.TryValidateHotGrantId("abc", out var ok));
        Assert.Equal("", ok);
    }

    [Fact]
    public void NormalizeGrantDefaults_assigns_missing_grantId()
    {
        var dto = new EffectGrantDto { EffectId = "fx.butter_on_hit" };
        EffectGrantSessionRecorder.NormalizeGrantDefaults(dto);
        Assert.False(string.IsNullOrWhiteSpace(dto.GrantId));
        Assert.Equal(EffectOwnerKeys.Match, dto.OwnerKey);
        Assert.Equal("match", dto.OwnerKind);
        Assert.Equal("debug", dto.PluginId);
    }
}
