using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

/// <summary>
/// Injector MatchHost contract without Unity: same MatchRuntime steps Host performs
/// (auto-end before start; Caps after start; end / match.result clear MatchKey;
/// NotifyPaused → observe Apply match.pause/resume).
/// </summary>
public class MatchInjectContractTests
{
    /// <summary>Mirror MatchHost.Apply fold order (no Effects / GameHooks).</summary>
    static void HostApply(MatchRuntime rt, string kind, IReadOnlyDictionary<string, object>? payload = null)
    {
        if (string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase) &&
            rt.Phase is not MatchPhase.Idle)
        {
            rt.Apply("board.end");
        }

        rt.Apply(kind, payload);
    }

    /// <summary>
    /// Mirror MatchHost.NotifyPaused: phase flip then Emit→Apply observe kind (Core no-op).
    /// Returns observe kind when transitioned; null when idempotent.
    /// </summary>
    static string? HostNotifyPaused(MatchRuntime rt, bool paused)
    {
        var before = rt.Phase;
        rt.NotifyPaused(paused);
        var after = rt.Phase;
        if (before == after) return null;

        var kind = paused ? "match.pause" : "match.resume";
        rt.Apply(kind);
        return kind;
    }

    /// <summary>Mirrors DebugRuntime.Snapshot nested <c>match</c> (W3-B) from MatchSnapshot.</summary>
    static Dictionary<string, object> NestedMatchObserve(MatchSnapshot snap) => new()
    {
        ["contractVersion"] = snap.ContractVersion,
        ["phase"] = snap.Phase.ToString(),
        ["matchKey"] = snap.MatchKey ?? "",
        ["revision"] = snap.Revision,
        ["plantCount"] = snap.PlantCount,
        ["zombieCount"] = snap.ZombieCount,
        ["bulletCount"] = snap.BulletCount,
        ["debugActive"] = snap.DebugActive,
        ["scenarioId"] = snap.ScenarioId ?? "",
        ["effectSessionActive"] = snap.EffectSessionActive,
        ["caps"] = new Dictionary<string, object>
        {
            ["maxLivingPlants"] = snap.Caps.MaxLivingPlants,
            ["maxLivingZombies"] = snap.Caps.MaxLivingZombies,
            ["maxLivingBullets"] = snap.Caps.MaxLivingBullets
        }
    };

    [Fact]
    public void InMatch_direct_board_start_ignored_phase_and_key_unchanged()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-keep" });
        var key = rt.MatchKey;
        var rev = rt.ToSnapshot().Revision;

        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-new" });

        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal(key, rt.MatchKey);
        Assert.Equal("m-keep", rt.MatchKey);
        Assert.Equal(rev, rt.ToSnapshot().Revision);
    }

    [Fact]
    public void InMatch_end_then_start_new_key_and_empty_board()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-old" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });
        Assert.Equal(1, rt.ToSnapshot().PlantCount);

        rt.Apply("board.end");
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);

        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-new" });
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-new", rt.MatchKey);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Host_auto_end_before_start_resets_like_MatchHost()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-a" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });

        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-b" });

        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-b", rt.MatchKey);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Start_ConfigureCaps_then_spawns_Admit_rejects_cap_plants()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start");
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 2, MaxLivingZombies = 80, MaxLivingBullets = -1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP2" });

        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.CapPlants, g.Reason);
    }

    [Fact]
    public void Host_start_ConfigureCaps_then_Admit_at_cap()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-cap" });
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 2, MaxLivingZombies = 80, MaxLivingBullets = -1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP2" });

        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.CapPlants, g.Reason);
        Assert.Equal(2, rt.ToSnapshot().Caps.MaxLivingPlants);
    }

    [Fact]
    public void Start_spawn_match_result_Idle_null_key_counts_zero()
    {
        var rt = new MatchRuntime();
        rt.Apply("board.start", new Dictionary<string, object> { ["matchKey"] = "m-res" });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });

        rt.Apply("match.result");

        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
        var snap = rt.ToSnapshot();
        Assert.Equal(0, snap.PlantCount);
        Assert.Equal(0, snap.ZombieCount);
    }

    [Fact]
    public void Idle_Admit_rejects_phase_idle()
    {
        var rt = new MatchRuntime();
        Assert.False(rt.TryAdmitSpawn("plant", out var g));
        Assert.Equal(GateReasons.PhaseIdle, g.Reason);
    }

    [Fact]
    public void Host_NotifyPaused_Admit_phase_paused_then_resume_Ok()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-pause" });
        Assert.True(rt.TryAdmitSpawn("plant", out var ok));
        Assert.True(ok.Ok);

        Assert.Equal("match.pause", HostNotifyPaused(rt, true));
        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.False(rt.TryAdmitSpawn("plant", out var paused));
        Assert.Equal(GateReasons.PhasePaused, paused.Reason);

        Assert.Equal("match.resume", HostNotifyPaused(rt, false));
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.True(rt.TryAdmitSpawn("plant", out var again));
        Assert.True(again.Ok);
    }

    [Fact]
    public void Host_NotifyPaused_transition_observe_Apply_noop_preserves_MatchKey()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-key" });
        var revBefore = rt.ToSnapshot().Revision;

        Assert.Equal("match.pause", HostNotifyPaused(rt, true));
        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.Equal("m-key", rt.MatchKey);
        var revPaused = rt.ToSnapshot().Revision;
        Assert.Equal(revBefore + 1, revPaused);

        // Apply("match.pause") already ran inside HostNotifyPaused — second Apply must not bump.
        rt.Apply("match.pause");
        Assert.Equal(revPaused, rt.ToSnapshot().Revision);
        Assert.Equal("m-key", rt.MatchKey);
        Assert.Equal(MatchPhase.Paused, rt.Phase);

        Assert.Equal("match.resume", HostNotifyPaused(rt, false));
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-key", rt.MatchKey);
        Assert.Equal(revPaused + 1, rt.ToSnapshot().Revision);

        rt.Apply("match.resume");
        Assert.Equal(revPaused + 1, rt.ToSnapshot().Revision);
        Assert.Equal("m-key", rt.MatchKey);
    }

    [Fact]
    public void Host_NotifyPaused_double_enter_no_second_observe()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-dup" });

        Assert.Equal("match.pause", HostNotifyPaused(rt, true));
        var rev = rt.ToSnapshot().Revision;

        Assert.Null(HostNotifyPaused(rt, true));
        Assert.Equal(MatchPhase.Paused, rt.Phase);
        Assert.Equal(rev, rt.ToSnapshot().Revision);
        Assert.Equal("m-dup", rt.MatchKey);
    }

    [Fact]
    public void DebugSnapshot_nested_match_mirrors_MatchSnapshot()
    {
        var rt = new MatchRuntime();
        HostApply(rt, "board.start", new Dictionary<string, object> { ["matchKey"] = "m-snap" });
        rt.ConfigureCaps(new CapPolicyConfig { MaxLivingPlants = 7, MaxLivingZombies = 9, MaxLivingBullets = -1 });
        rt.Apply("plant.spawn", new Dictionary<string, object> { ["ptr"] = "0xP1" });
        rt.NotifyPaused(true);

        var snap = rt.ToSnapshot();
        var nested = NestedMatchObserve(snap);

        Assert.Equal(MatchRuntime.ContractVersion, nested["contractVersion"]);
        Assert.Equal("Paused", nested["phase"]);
        Assert.Equal("m-snap", nested["matchKey"]);
        Assert.Equal(snap.Revision, nested["revision"]);
        Assert.Equal(1, nested["plantCount"]);
        Assert.Equal(0, nested["zombieCount"]);
        Assert.Equal(0, nested["bulletCount"]);
        Assert.Equal(snap.DebugActive, nested["debugActive"]);
        Assert.Equal(snap.ScenarioId ?? "", nested["scenarioId"]);
        Assert.Equal(snap.EffectSessionActive, nested["effectSessionActive"]);

        var caps = Assert.IsType<Dictionary<string, object>>(nested["caps"]);
        Assert.Equal(7, caps["maxLivingPlants"]);
        Assert.Equal(9, caps["maxLivingZombies"]);
        Assert.Equal(-1, caps["maxLivingBullets"]);
    }
}
