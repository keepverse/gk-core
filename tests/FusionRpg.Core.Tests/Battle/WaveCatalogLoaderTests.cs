using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// base-defense `siege-waves` §3.5 (task 12.4, 2026-09-06): wave composition moves from a hand-written
/// `WaveCatalog.Build()` array into <c>gk-core/data/tuning/waves.v1.json</c>. This file loads the REAL
/// committed data file through the real <see cref="WaveCatalogLoader"/>, against the real compiled
/// species roster — no hand-built fixture — matching <c>SpeciesBuildPlanCatalogRealFileTests</c>'s own
/// established <c>RepoRoot()</c> convention. The acceptance is byte-identity with the compiled roster
/// `WaveCatalog.Build()` still produces, proven directly rather than argued.
///
/// <para>Deliberately compares against <see cref="WaveCatalog.Build"/> directly rather than going
/// through <see cref="WaveCatalog.Configure"/>/<see cref="WaveCatalog.All"/> — those mutate shared,
/// process-wide static state that a concurrently-running, unrelated test class could observe mid-call
/// (xUnit's default cross-class parallelism), so every test here is a pure comparison with no shared
/// mutation, except the two tests whose own subject IS `Configure`'s behaviour.</para>
/// </summary>
public class WaveCatalogLoaderTests
{
    static string RepoRoot()
    {
        // Resolver contract (tasks/keepverse-split-plan.md "Resolver contract"; gk-core/tests/Shared/KeepverseRoots.cs):
        // the engine repo root, so a `gk-core/data/tuning` or `src/` read stays valid once the injector source moves
        // to gk-fusion. Hunting for that directory was a private, split-fragile root signal.
        return FusionRpg.TestSupport.CoreRoot.Path;
    }

    static string RealJson() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "waves.v1.json"));

    [Fact]
    public void The_real_data_file_parses_to_exactly_the_compiled_default_rosters_own_shape()
    {
        var compiled = WaveCatalog.Build();
        var parsed = WaveCatalogLoader.Parse(RealJson());

        Assert.Equal(compiled.Count, parsed.Count);
        for (var i = 0; i < compiled.Count; i++)
        {
            var c = compiled[i];
            var p = parsed[i];
            Assert.Equal(c.WaveId, p.WaveId);
            Assert.Equal(c.Name, p.Name);
            Assert.Equal(c.ContentIndex, p.ContentIndex);
            Assert.Equal(c.Profile, p.Profile);
            Assert.Equal(c.W, p.W);
            Assert.Equal(c.Enemies.Count, p.Enemies.Count);
            for (var j = 0; j < c.Enemies.Count; j++)
            {
                // Reference equality is not expected (two independently-built lists) -- compare the
                // actual battle-facing fields instead, which is what "byte-identical golden" means.
                Assert.Equal(c.Enemies[j].Key, p.Enemies[j].Key);
                Assert.Equal(c.Enemies[j].SpeciesId, p.Enemies[j].SpeciesId);
                Assert.Equal(c.Enemies[j].TypeId, p.Enemies[j].TypeId);
                Assert.Equal(c.Enemies[j].Level, p.Enemies[j].Level);
                Assert.Equal(c.Enemies[j].MaxHp, p.Enemies[j].MaxHp);
                Assert.Equal(c.Enemies[j].Atk, p.Enemies[j].Atk);
                Assert.Equal(c.Enemies[j].Defense, p.Enemies[j].Defense);
            }
        }
    }

    [Fact]
    public void The_real_data_file_names_all_four_shipped_waves_by_id()
    {
        var parsed = WaveCatalogLoader.Parse(RealJson());
        Assert.Equal(
            new[] { "rift-skirmish", "rift-warband", "rift-onslaught", "rift-tyrant" },
            parsed.Select(w => w.WaveId).ToArray());
    }

    [Fact]
    public void Configure_rejects_an_empty_snapshot_without_touching_shared_state()
    {
        // Never assigns `_all` on the throwing path (checked before the assignment in
        // WaveCatalog.Configure) -- safe to call from any test, any time, with no reset needed.
        Assert.Throws<ArgumentException>(() => WaveCatalog.Configure(Array.Empty<WaveDef>()));
    }

    [Fact]
    public void Configure_then_ConfigureFromCompiledDefault_round_trips_All_back_to_the_compiled_roster()
    {
        // The one test that touches shared state, kept minimal and immediately restored. A
        // concurrently-running unrelated test could in principle observe the overridden roster mid-call
        // -- the same class of risk this codebase already accepts for CreatureSpeciesCatalog.Configure,
        // called from several existing test files the same way.
        var compiledCount = WaveCatalog.Build().Count;
        try
        {
            WaveCatalog.Configure(WaveCatalogLoader.Parse(RealJson()));
            Assert.Equal(compiledCount, WaveCatalog.All.Count);
            Assert.Equal("Rift Tyrant", WaveCatalog.Get("rift-tyrant").Name);
        }
        finally
        {
            WaveCatalog.ConfigureFromCompiledDefault();
        }

        Assert.Equal(compiledCount, WaveCatalog.All.Count);
    }

    [Fact]
    public void Parse_rejects_a_missing_waves_array()
    {
        Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse("{}"));
    }

    [Fact]
    public void Parse_rejects_an_unknown_rarity()
    {
        var bad = """{"waves":[{"waveId":"x","name":"X","contentIndex":1,"picks":[{"rarityFrom":"mythic","rarityTo":"chaff","count":1,"sameSpeciesMaxMilli":0,"weights":{"chaff":100}}]}]}""";
        Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse(bad));
    }

    [Fact]
    public void Parse_rejects_a_non_positive_pick_count()
    {
        var bad = """{"waves":[{"waveId":"x","name":"X","contentIndex":1,"picks":[{"rarityFrom":"chaff","rarityTo":"chaff","count":0,"sameSpeciesMaxMilli":0,"weights":{"chaff":100}}]}]}""";
        Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse(bad));
    }

    [Fact]
    public void Parse_rejects_a_wave_with_no_picks()
    {
        var bad = """{"waves":[{"waveId":"x","name":"X","contentIndex":1,"picks":[]}]}""";
        Assert.Throws<WaveCatalogRejection>(() => WaveCatalogLoader.Parse(bad));
    }

    [Fact]
    public void Parse_honours_an_explicit_w_when_the_data_sets_one()
    {
        var withW = """{"waves":[{"waveId":"x","name":"X","contentIndex":1,"w":2,"picks":[{"rarityFrom":"chaff","rarityTo":"chaff","count":1,"sameSpeciesMaxMilli":0,"weights":{"chaff":100}}]}]}""";
        var parsed = WaveCatalogLoader.Parse(withW);
        Assert.Equal(2, parsed[0].W);
    }
}
