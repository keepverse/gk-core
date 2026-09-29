using System.Text.Json.Nodes;
using FusionRpg.Core.Items.Mutation;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T41 (`craft-assurance` c) — the strict loader over the REAL shipped
/// `gk-core/data/tuning/craft-assurance.v1.json`, and its host-injected Hub. Every fixture that mutates the
/// Hub restores it via `try/finally` (never a bare `Reset()` left standing) — this Hub has no other
/// reader yet (T42/T44's own scope), so nothing else in this assembly depends on its configured
/// state, but the discipline is the same this codebase's every other Hub test already holds.
/// </summary>
public class CraftAssuranceTuningTests
{
    static string RealJson() => File.ReadAllText(
        Path.Combine(MaterialCorpusTests.RepoRoot(), "data", "tuning", "craft-assurance.v1.json"));

    static string Mutated(Action<JsonNode> mutate)
    {
        var root = JsonNode.Parse(RealJson())!;
        mutate(root);
        return root.ToJsonString();
    }

    [Fact]
    public void The_real_shipped_file_parses_both_ratios_in_range()
    {
        var t = CraftAssuranceTuning.Parse(RealJson());
        Assert.InRange(t.AssureBonusMilli, 0, 1000);
        Assert.InRange(t.RepairCoverageBonusMilli, 0, 1000);
    }

    [Fact]
    public void A_missing_assure_bonus_key_throws()
    {
        var json = Mutated(root => root.AsObject().Remove("assureBonusMilli"));
        var ex = Assert.Throws<CraftAssuranceTuningRejection>(() => CraftAssuranceTuning.Parse(json));
        Assert.Contains("assureBonusMilli", ex.Message);
    }

    [Fact]
    public void A_missing_repair_coverage_key_throws()
    {
        var json = Mutated(root => root.AsObject().Remove("repairCoverageBonusMilli"));
        var ex = Assert.Throws<CraftAssuranceTuningRejection>(() => CraftAssuranceTuning.Parse(json));
        Assert.Contains("repairCoverageBonusMilli", ex.Message);
    }

    [Theory]
    [InlineData("assureBonusMilli", 1001)]
    [InlineData("assureBonusMilli", -1)]
    [InlineData("repairCoverageBonusMilli", 1001)]
    [InlineData("repairCoverageBonusMilli", -1)]
    public void A_ratio_outside_0_to_1000_is_a_load_rejection_naming_the_key(string key, int badValue)
    {
        var json = Mutated(root => root[key] = badValue);
        var ex = Assert.Throws<CraftAssuranceTuningRejection>(() => CraftAssuranceTuning.Parse(json));
        Assert.Contains(key, ex.Message);
    }

    [Fact]
    public void A_non_integer_ratio_throws()
    {
        var json = Mutated(root => root["assureBonusMilli"] = "fifty");
        Assert.Throws<CraftAssuranceTuningRejection>(() => CraftAssuranceTuning.Parse(json));
    }

    [Fact]
    public void Empty_document_is_refused()
    {
        Assert.Throws<CraftAssuranceTuningRejection>(() => CraftAssuranceTuning.Parse(""));
    }

    // ---- the Hub -------------------------------------------------------------------------------------

    [Fact]
    public void The_hub_throws_by_name_before_configure_has_run()
    {
        CraftAssuranceTuningHub.Reset();
        try
        {
            Assert.False(CraftAssuranceTuningHub.IsConfigured);
            var ex = Assert.Throws<InvalidOperationException>(() => CraftAssuranceTuningHub.Tuning);
            Assert.Contains("CraftAssuranceTuningHub.Configure", ex.Message);
        }
        finally { CraftAssuranceTuningHub.Reset(); }
    }

    [Fact]
    public void The_hub_is_re_assignable_never_throwing_on_a_second_configure()
    {
        var real = CraftAssuranceTuning.Parse(RealJson());
        try
        {
            CraftAssuranceTuningHub.Configure(real);
            Assert.True(CraftAssuranceTuningHub.IsConfigured);
            Assert.Same(real, CraftAssuranceTuningHub.Tuning);

            var second = CraftAssuranceTuning.Parse(RealJson());
            CraftAssuranceTuningHub.Configure(second); // must not throw on a second call
            Assert.Same(second, CraftAssuranceTuningHub.Tuning);
        }
        finally { CraftAssuranceTuningHub.Reset(); }
    }

    [Fact]
    public void Configure_refuses_a_null_tuning()
    {
        Assert.Throws<ArgumentNullException>(() => CraftAssuranceTuningHub.Configure(null!));
    }
}
