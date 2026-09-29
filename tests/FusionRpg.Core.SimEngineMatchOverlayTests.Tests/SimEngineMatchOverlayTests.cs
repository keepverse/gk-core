using FusionRpg.Contracts;
using FusionRpg.Core;
using FusionRpg.Core.Match;
using Xunit;

namespace FusionRpg.Core.Tests;

public class SimEngineMatchOverlayTests
{
    [Fact]
    public void EnableMatchOverlay_folds_spawn_die_end()
    {
        var eng = new SimEngine();
        var rt = eng.EnableMatchOverlay();
        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-sim-1", LevelName = "L" });
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-sim-1", rt.MatchKey);

        eng.SpawnPlant(new StatsConfig { ApplyStats = false }, new SimSpawnPlantRequest
        {
            Ptr = "P1",
            Type = 0,
            Hp = 100,
            MaxHp = 100
        });
        Assert.Equal(1, rt.ToSnapshot().PlantCount);

        eng.DiePlant(new SimDieRequest { Ptr = "P1" });
        Assert.Equal(0, rt.ToSnapshot().PlantCount);

        eng.BoardEnd(new SimBoardEndRequest { Summary = new Dictionary<string, object>() });
        Assert.Equal(MatchPhase.Idle, rt.Phase);
        Assert.Null(rt.MatchKey);
    }

    [Fact]
    public void Place_does_not_inflate_overlay_living_count()
    {
        var eng = new SimEngine();
        var rt = eng.EnableMatchOverlay();
        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-place" });
        eng.PlacePlant(new SimPlacePlantRequest { Ptr = "P9", Type = 0, Col = 1, Row = 1 });
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
    }

    [Fact]
    public void Replay_SimResult_events_matches_overlay()
    {
        var eng = new SimEngine();
        eng.EnableMatchOverlay();
        var start = eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-rep" });
        var spawn = eng.SpawnZombie(new StatsConfig { ApplyStats = false }, new SimSpawnZombieRequest
        {
            Ptr = "Z1",
            Type = 0,
            Hp = 50,
            MaxHp = 50
        });

        var events = start.Events.Concat(spawn.Events);
        var viaReplay = MatchValidator.Replay(events);
        var viaOverlay = eng.MatchOverlay!.ToSnapshot();

        Assert.Equal(viaOverlay.Phase, viaReplay.Phase);
        Assert.Equal(viaOverlay.ZombieCount, viaReplay.ZombieCount);
        Assert.Equal(viaOverlay.MatchKey, viaReplay.MatchKey);
    }

    [Fact]
    public void Second_BoardStart_without_BoardEnd_resets_overlay()
    {
        var eng = new SimEngine();
        var rt = eng.EnableMatchOverlay();
        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-a" });
        eng.SpawnPlant(new StatsConfig { ApplyStats = false }, new SimSpawnPlantRequest
        {
            Ptr = "P1",
            Type = 0,
            Hp = 10,
            MaxHp = 10
        });
        Assert.Equal(1, rt.ToSnapshot().PlantCount);

        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-b" });
        Assert.Equal(MatchPhase.InMatch, rt.Phase);
        Assert.Equal("m-b", rt.MatchKey);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);

        eng.SpawnPlant(new StatsConfig { ApplyStats = false }, new SimSpawnPlantRequest
        {
            Ptr = "P2",
            Type = 0,
            Hp = 10,
            MaxHp = 10
        });
        var snap = rt.ToSnapshot();
        Assert.Equal(1, snap.PlantCount);
        Assert.Contains(snap.Entities, e => e.Ptr == "P2");
        Assert.DoesNotContain(snap.Entities, e => e.Ptr == "P1");
    }

    [Fact]
    public void DisableMatchOverlay_stops_folding()
    {
        var eng = new SimEngine();
        var rt = eng.EnableMatchOverlay();
        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-dis" });
        eng.DisableMatchOverlay();
        var rev = rt.Revision;
        eng.SpawnPlant(new StatsConfig { ApplyStats = false }, new SimSpawnPlantRequest
        {
            Ptr = "P9",
            Type = 0,
            Hp = 10,
            MaxHp = 10
        });
        Assert.Equal(rev, rt.Revision);
        Assert.Equal(0, rt.ToSnapshot().PlantCount);
        Assert.Single(eng.Plants);
    }

    [Fact]
    public void Skipped_duplicate_spawn_does_not_change_overlay_count()
    {
        var eng = new SimEngine();
        var rt = eng.EnableMatchOverlay();
        eng.BoardStart(new SimBoardStartRequest { MatchKey = "m-skip" });
        var stats = new StatsConfig { ApplyStats = false };
        eng.SpawnPlant(stats, new SimSpawnPlantRequest { Ptr = "P1", Type = 0, Hp = 10, MaxHp = 10 });
        var rev = rt.Revision;
        var skipped = eng.SpawnPlant(stats, new SimSpawnPlantRequest { Ptr = "P1", Type = 0, Hp = 10, MaxHp = 10 });
        Assert.True(skipped.Skipped);
        Assert.Equal(1, rt.ToSnapshot().PlantCount);
        Assert.Equal(rev, rt.Revision);
    }
}

public class MatchDataBanGuardTests
{
    [Fact]
    public void FusionRpg_Core_csproj_has_no_Data_ProjectReference()
    {
        var root = FindRepoRoot();
        var csproj = Path.Combine(root, "src", "FusionRpg.Core", "FusionRpg.Core.csproj");
        Assert.True(File.Exists(csproj), "missing " + csproj);
        var text = File.ReadAllText(csproj);
        Assert.DoesNotContain("FusionRpg.Data", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Match_assembly_types_do_not_reference_FusionRpg_Data()
    {
        var matchAsm = typeof(MatchRuntime).Assembly;
        foreach (var type in matchAsm.GetTypes())
        {
            if (type.Namespace == null || !type.Namespace.StartsWith("FusionRpg.Core.Match", StringComparison.Ordinal))
                continue;
            foreach (var field in type.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                Assert.False(IsDataType(field.FieldType), type.FullName + "." + field.Name);
            foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                Assert.False(IsDataType(prop.PropertyType), type.FullName + "." + prop.Name);
        }
    }

    static bool IsDataType(Type t)
    {
        if (t.FullName != null && t.FullName.StartsWith("FusionRpg.Data", StringComparison.Ordinal))
            return true;
        if (t.IsGenericType)
        {
            foreach (var arg in t.GetGenericArguments())
                if (IsDataType(arg)) return true;
        }

        return false;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "FusionRpg.Core", "FusionRpg.Core.csproj");
            if (File.Exists(candidate)) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate FusionRpg.Core.csproj from " + AppContext.BaseDirectory);
    }
}
