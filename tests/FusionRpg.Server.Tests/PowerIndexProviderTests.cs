using System;
using System.IO;
using System.Text.RegularExpressions;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Power;
using FusionRpg.Core.Progression;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Data;
using FusionRpg.Data.Sqlite;
using FusionRpg.Data.Tests;
using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// `ai-empire-species` EP4.16 (R23) — `ServerPowerIndexProvider.ActorIndexFor(SaveId, EmpireId)`:
/// Zomboss's Theta through the SAME `<see cref="PowerIndexComposer.ActorExplain"/>` and the SAME ladder
/// the player's Theta already goes through, over that empire's OWN commander level
/// (`RpgStore.CommanderLevelOf`, `zomboss-commander-clock` SP7.3's one seam). A wiring gap closed, never
/// a new curve — which is what the `No_private_curve` case pins.
/// </summary>
public class PowerIndexProviderTests : IDisposable
{
    readonly DataTestStore _testStore;
    readonly RpgStore _store;
    readonly PowerTuning _tuning;
    readonly FusionRpg.Server.Power.ServerPowerIndexProvider _provider;

    public PowerIndexProviderTests()
    {
        _testStore = DataTestStore.Create();
        _store = _testStore.Store;
        _tuning = PowerTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "power-scale.v2.json")));
        // This assembly's [ModuleInitializer] bootstrap (PowerAndAptitudeTuningTestBootstrap) covers
        // Power/Aptitude/DerivedStatPolicy, not the progression curve the existing ActorIndex(player)
        // read hydrates through (RpgXpCurve) — configured here so the byte-identical comparison below
        // exercises the real read rather than a stub.
        ProgressionTuningHub.Configure(ProgressionTuningLoader.Parse(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "data", "tuning", "progression.v3.json"))));
        _provider = new FusionRpg.Server.Power.ServerPowerIndexProvider(_store, _tuning);
    }

    public void Dispose() => _testStore.Dispose();

    /// <summary>Writes a commander row directly — the STATE `zomboss-commander-clock` leaves — so the
    /// read can be tested without driving a whole run-completion fact through the XP math.</summary>
    void SeedCommanderLevel(long saveId, string empireId, long level)
    {
        using var db = SqliteConnectionFactory.Open(_store.HotPath);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rpg_actor_progression(
              save_id, empire_id, kind, type_id, level, xp, highest_level, demotion_count, revision, updated_utc)
            VALUES ($s, $e, 'player', 0, $l, 0, $l, 0, 0, $t);
            """;
        cmd.Parameters.AddWithValue("$s", saveId);
        cmd.Parameters.AddWithValue("$e", empireId);
        cmd.Parameters.AddWithValue("$l", level);
        cmd.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void ActorIndexFor_composes_the_empires_own_commander_level_through_the_one_ladder()
    {
        var player = _store.CreatePlayer("PowerIndexEmpire");
        SeedCommanderLevel(player.Id, EmpireId.Zomboss.Value, 12);
        SeedCommanderLevel(player.Id, EmpireId.Dave.Value, 1);

        var expected = PowerIndexComposer.ActorExplain(
            _tuning, new ActorLadderSnapshot(12, RealmsAdvanced: 0, PvzRuns: 0)).Total;
        var zomboss = _provider.ActorIndexFor(new SaveId(player.Id), EmpireId.Zomboss);
        var dave = _provider.ActorIndexFor(new SaveId(player.Id), EmpireId.Dave);

        // The composer's own answer at that level, not a pinned magnitude: plan/tuning own P(Theta).
        Assert.Equal(expected, zomboss);
        Assert.True(zomboss > dave,
            "level 12 must compose above level 1 — a read that ignored the empire's own row would not");
    }

    [Fact]
    public void ActorIndexFor_the_human_empire_is_the_existing_player_read_unchanged()
    {
        // The existing ActorIndex(player) reads the same kind='player', type_id=0 row, so the two must
        // agree for the human empire. This is the "byte-identical" clause: adding the empire-keyed read
        // moved nothing the player's own Theta already returned.
        var player = _store.CreatePlayer("PowerIndexHuman");
        SeedCommanderLevel(player.Id, EmpireId.Dave.Value, 12);

        var viaPlayer = _provider.ActorIndex(new StatContext { PlayerId = player.Id });
        var viaEmpire = _provider.ActorIndexFor(new SaveId(player.Id), EmpireId.Dave);

        Assert.Equal(viaPlayer, viaEmpire);
        Assert.True(viaPlayer > 0, "the demo row must be above level 1, or this proves nothing");
    }

    [Fact]
    public void ActorIndexFor_introduces_no_private_curve()
    {
        // Structural, and the reason this is a SEPARATE case from the composer equality above: the
        // equality proves today's answer; this proves the SHAPE that makes it stay true — the method
        // reads the level and hands it to the composer, with no arithmetic of its own (guard-power G2's
        // own heuristic, applied to this one method instead of the whole repo).
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "FusionRpg.Server", "Power", "ServerPowerIndexProvider.cs"));
        var body = MethodBody(source, "public int ActorIndexFor(SaveId save, EmpireId empire)");

        Assert.Contains("_store.CommanderLevelOf(save, empire)", body, StringComparison.Ordinal);
        Assert.Contains("ActorLadderSnapshot(", body, StringComparison.Ordinal);
        Assert.Contains("PowerIndexComposer.ActorExplain(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.", body, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"[*^]"), body);
    }

    static string MethodBody(string source, string signature)
    {
        // An EXPRESSION-BODIED member (`=> …;`) has no braces of its own, so the block search below
        // would pick up some later method's braces and assert against the wrong text. Bound by the
        // next member declaration instead, which is exact for both shapes.
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration not found: {signature}");
        var next = source.IndexOf("\n    public ", start + signature.Length, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root above " + AppContext.BaseDirectory);
    }
}
