using System.Runtime.CompilerServices;
using FusionRpg.Core.Aura;
using Xunit;

namespace FusionRpg.Core.Tests.Aura;

/// <summary>aura-skill T10: `AuraTuningLoader` is a pure parser for `data/tuning/aura.v{n}.json`
/// (`spec-aura-magnitude.md` §3.4) — the declared rung→k mapping. No aura may exist below rung 7 (the
/// `consumption` upkeep floor) or above rung 10 (`action-rungs.v1.json`'s own cap) — both rejected AT
/// LOAD, never discovered later.</summary>
public class AuraTuningTests
{
    const string ValidJson = """
        { "schemaVersion": 1, "version": 1, "rungMapping": { "7": 5359, "8": 7090, "9": 9379, "10": 12407 }, "maxActiveAuras": 1 }
        """;

    // Matches RungTableTests' own established convention for locating a real gk-core/data/tuning file.
    static string TuningPath([CallerFilePath] string here = "")
    {
        var testsDir = Path.GetDirectoryName(here)!;                            // tests/.../Aura
        var repo = Path.GetFullPath(Path.Combine(testsDir, "..", "..", "..")); // repo root
        return Path.Combine(repo, "data", "tuning", "aura.v1.json");
    }

    [Fact]
    public void The_real_shipped_aura_tuning_file_parses_and_matches_action_rungs_qPowerMilli()
    {
        var tuning = AuraTuningLoader.Parse(File.ReadAllText(TuningPath()));

        // Mirrors action-rungs.v1.json's own qPowerMilli values for rungs 7-10 verbatim -- the
        // aura program's k(rung) axis reuses the shipped power ladder, it does not author a second one.
        Assert.Equal(5359, tuning.KMilliFor(7));
        Assert.Equal(7090, tuning.KMilliFor(8));
        Assert.Equal(9379, tuning.KMilliFor(9));
        Assert.Equal(12407, tuning.KMilliFor(10));

        // T13: maxActiveAuras=1 by default (owner decision Q8) -- not blocking at N=1.
        Assert.Equal(1, tuning.MaxActiveAuras);
    }

    [Fact]
    public void Missing_maxActiveAuras_is_rejected_at_load()
    {
        const string json = """
            { "rungMapping": { "7": 5359 } }
            """;
        var ex = Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse(json));
        Assert.Contains("maxActiveAuras", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_positive_maxActiveAuras_is_rejected_at_load()
    {
        const string json = """
            { "rungMapping": { "7": 5359 }, "maxActiveAuras": 0 }
            """;
        var ex = Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse(json));
        Assert.Contains("maxActiveAuras", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rung_below_7_is_rejected_at_load()
    {
        const string json = """
            { "rungMapping": { "6": 4051, "7": 5359, "8": 7090, "9": 9379, "10": 12407 } }
            """;
        var ex = Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse(json));
        Assert.Contains("rung 6", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rung_above_10_is_rejected_at_load()
    {
        const string json = """
            { "rungMapping": { "7": 5359, "11": 16000 } }
            """;
        var ex = Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse(json));
        Assert.Contains("rung 11", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_mapping_is_rejected_at_load()
    {
        Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse("{ \"rungMapping\": {} }"));
    }

    [Fact]
    public void A_non_positive_k_is_rejected_at_load()
    {
        Assert.Throws<AuraTuningRejection>(() => AuraTuningLoader.Parse("{ \"rungMapping\": { \"7\": 0 } }"));
    }

    [Fact]
    public void KMilliFor_an_undeclared_rung_throws_even_after_a_valid_load()
    {
        var tuning = AuraTuningLoader.Parse(ValidJson);
        Assert.Throws<ArgumentOutOfRangeException>(() => tuning.KMilliFor(5));
    }
}
