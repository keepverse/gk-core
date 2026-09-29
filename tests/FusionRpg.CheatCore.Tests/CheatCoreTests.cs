using FusionRpg.CheatCore;
using Xunit;

namespace FusionRpg.CheatCore.Tests;

public class CheatRegistryTests
{
    [Fact]
    public void EnsureDefaults_registers_coverage_ids()
    {
        var r = new CheatRegistry();
        r.EnsureDefaults();
        Assert.True(r.On("A-APPLY"));
        Assert.Equal(1f, r.FVal("A-P-HP%"));
        Assert.False(r.On("P-GOD"));
        Assert.True(r.On("SYS-EMIT-PROOF"));
        Assert.True(r.On("SYS-DAMAGE-FX"));
        Assert.False(r.On("SYS-LIMHEALTH-GATE"));
        Assert.Contains(r.Entries.Keys, k => k.StartsWith("E-"));
        Assert.Contains(r.Entries.Keys, k => k.StartsWith("Z-SLOW-"));
    }

    [Fact]
    public void SetFloat_E_locks_board_config()
    {
        var r = new CheatRegistry();
        r.EnsureDefaults();
        Assert.False(r.BoardConfigLocked);
        r.SetFloat("E-ZH", 2f);
        Assert.True(r.BoardConfigLocked);
        Assert.Equal(2f, r.FVal("E-ZH"));
    }

    [Fact]
    public void ResetGroup_A_preserves_other_prefixes()
    {
        var r = new CheatRegistry();
        r.EnsureDefaults();
        r.SetFloat("A-P-HP%", 5f);
        r.SetToggle("P-GOD", true);
        r.ResetGroup("A-");
        Assert.Equal(1f, r.FVal("A-P-HP%"));
        Assert.True(r.On("P-GOD"));
    }

    [Fact]
    public void Snapshot_round_trip()
    {
        var r = new CheatRegistry();
        r.EnsureDefaults();
        r.SetToggle("Z-GOD", true);
        r.SetFloat("E-ZD", 3f);
        var snap = r.Snapshot();
        var r2 = new CheatRegistry();
        r2.EnsureDefaults();
        var entries = ((List<Dictionary<string, object>>)snap["entries"])
            .Select(d => (
                (string)d["id"],
                (bool)d["enabled"],
                Convert.ToDouble(d["floatValue"])));
        r2.ApplySnapshot(entries, (bool)snap["boardConfigLocked"]);
        Assert.True(r2.On("Z-GOD"));
        Assert.Equal(3f, r2.FVal("E-ZD"));
        Assert.True(r2.BoardConfigLocked);
    }
}

public class SpawnCatalogCoreTests
{
    [Fact]
    public void Note_skips_Nothing_and_marks_spawn()
    {
        var c = new SpawnCatalogCore();
        c.Note("plant", 0, "Nothing", "x");
        Assert.Equal(0, c.Count("plant"));
        c.Note("plant", 1, "Peashooter", "place");
        c.Note("plant", 1, "Peashooter", "almanac");
        Assert.Equal(1, c.Count("plant"));
        c.MarkSpawn("plant", 1, false, "fail");
        var list = c.List("plant");
        Assert.False(list[0].SpawnOk);
        Assert.Equal(2, list[0].Sources.Count);
        c.ClearFailed();
        Assert.Null(c.List("plant")[0].SpawnOk);
    }
}

public class CheatActionNamesTests
{
    [Fact]
    public void Known_actions_include_push_now_and_clear_failed()
    {
        Assert.True(CheatActionNames.IsKnown("push-now"));
        Assert.True(CheatActionNames.IsKnown("clear-failed"));
        Assert.True(CheatActionNames.IsKnown("travel-buff"));
        Assert.True(CheatActionNames.IsKnown("run-pack"));
        Assert.False(CheatActionNames.IsKnown("not-a-real-action"));
        Assert.True(CheatActionNames.All.Count >= 30);
    }
}

public class ProbePacksTests
{
    [Fact]
    public void All_packs_have_steps_and_expected_kinds()
    {
        Assert.NotEmpty(ProbePacks.All);
        Assert.NotNull(ProbePacks.Get("pack.smoke-core"));
        foreach (var p in ProbePacks.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(p.Id));
            Assert.NotEmpty(p.Steps);
            Assert.NotEmpty(p.ExpectedKinds);
            Assert.Contains("cheat.inject", p.ExpectedKinds);
        }
    }
}
