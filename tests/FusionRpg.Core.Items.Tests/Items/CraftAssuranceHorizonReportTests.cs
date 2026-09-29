using FusionRpg.Core.Items.Drops;
using FusionRpg.Core.Items.Materials;
using FusionRpg.Core.Items.Mutation;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// species-gear-chain T46 (`craft-assurance` h) — the gamble-vs-assurance report, driven against the
/// REAL shipped `enhancement.v1.json`, the REAL shipped `craft-assurance.v1.json` and the REAL shipped
/// loot corpus (`gk-data/packs/fusion/data/seed/loot/**`, the same one `DropVolumeCorpusTests` reads). Per the task's own
/// acceptance criteria, <b>no test here asserts a ratio, a drop count or a stock level</b> — the right
/// number is a balance judgement (validation-ssot.md), and every assertion below is either "it ran
/// without throwing" or a check against a CLOSED vocabulary (the three assurance ids, a fixed set the
/// code owns), never a numeric reading.
/// </summary>
public class CraftAssuranceHorizonReportTests
{
    static EnhancementTuning Enhance() => EnhancementTuning.Parse(
        File.ReadAllText(Path.Combine(DropVolumeTests.RepoRoot(), "data", "tuning", "enhancement.v1.json")));

    static CraftAssuranceTuning Assurance() => CraftAssuranceTuning.Parse(
        File.ReadAllText(Path.Combine(DropVolumeTests.RepoRoot(), "data", "tuning", "craft-assurance.v1.json")));

    [Fact]
    public void The_report_renders_from_shipped_tuning_and_the_real_corpus_with_no_authored_input()
    {
        var enhance = Enhance();
        var assurance = Assurance();
        var corpus = DropVolumeCorpusTests.Corpus();
        var byId = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);

        var assureId = MaterialCatalog.AssuranceId("assure");
        var bossTable = byId["drop.web.wave-boss"];
        var assureYieldPerMille = CraftAssuranceHorizonReport.ExpectedRefIdPerMille(
            bossTable, assureId, itemLevel: 10, id => byId.TryGetValue(id, out var t) ? t : null);

        var maxLevel = EnhancePolicy.MaxLevelForItemLevel(itemLevel: 10, enhance);
        var sourceYield = CraftAssuranceHorizonReport.PerSourceYield(corpus.Sources, byId);

        var text = CraftAssuranceHorizonReport.Render(
            1, maxLevel, enhance, assurance.AssureBonusMilli, assureYieldPerMille, sourceYield);

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.Contains("gamble-vs-assurance", text, StringComparison.Ordinal);
        Assert.Contains("per-source assurance yield", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_row_from_one_to_the_real_max_level_computes_without_throwing()
    {
        var enhance = Enhance();
        var assurance = Assurance();
        var maxLevel = EnhancePolicy.MaxLevelForItemLevel(itemLevel: 10, enhance);

        for (var level = 1; level <= maxLevel; level++)
            CraftAssuranceHorizonReport.Row(level, enhance, assurance.AssureBonusMilli, assureYieldPerMille: 300);
    }

    [Fact]
    public void Per_source_yield_covers_every_shipped_source_and_all_three_assurance_ids()
    {
        var corpus = DropVolumeCorpusTests.Corpus();
        var byId = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);

        var rows = CraftAssuranceHorizonReport.PerSourceYield(corpus.Sources, byId);

        // Structural: one row per (source, verb) pair for every source whose table resolves — never
        // a reading on the YIELD values themselves.
        var resolvedSources = corpus.Sources.Count(s => byId.ContainsKey(s.TableId));
        Assert.Equal(resolvedSources * MaterialCatalog.AssuranceVerbs.Count, rows.Count);
        Assert.All(MaterialCatalog.AssuranceVerbs, verb =>
            Assert.Contains(rows, r => r.MaterialId == MaterialCatalog.AssuranceId(verb)));
    }

    [Fact]
    public void The_boss_table_yields_more_than_zero_for_all_three_ids_after_t45()
    {
        // Not a ratio assertion (validation-ssot) -- a CONTRACT check that T45's own authored entries
        // are reachable at all, the same "did the wiring happen" shape T34d-wire's own test used.
        var corpus = DropVolumeCorpusTests.Corpus();
        var byId = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);
        var bossTable = byId["drop.web.wave-boss"];

        foreach (var verb in MaterialCatalog.AssuranceVerbs)
        {
            var refId = MaterialCatalog.AssuranceId(verb);
            var milli = CraftAssuranceHorizonReport.ExpectedRefIdPerMille(
                bossTable, refId, itemLevel: 10, id => byId.TryGetValue(id, out var t) ? t : null);
            Assert.True(milli > 0, $"'{refId}' yields nothing from '{bossTable.TableId}'");
        }
    }

    [Fact]
    public void A_zero_yield_source_prints_long_max_value_rather_than_dividing_by_zero()
    {
        var enhance = Enhance();
        var assurance = Assurance();

        var row = CraftAssuranceHorizonReport.Row(1, enhance, assurance.AssureBonusMilli, assureYieldPerMille: 0);

        // R-G1's first failure mode ("reachable only in theory"), printed literally -- never a throw,
        // never a silently wrong finite number.
        if (row.AssureChargesForCertainty > 0)
            Assert.Equal(long.MaxValue, row.ExpectedBossKillsForCertaintyMilli);
    }

    // ── § Design 7's two columns the shipped lower bound left out (downgrade risk, craft wear) ──────

    [Fact]
    public void Downgrade_risk_is_exactly_the_geometric_mean_where_a_failure_cannot_fall()
    {
        var enhance = Enhance();

        // Below the floor a failure never downgrades, so the two columns MUST agree -- an exact
        // relation, not a reading.
        for (var level = 1; level < enhance.DowngradeFromLevel; level++)
        {
            var success = EnhancePolicy.SuccessMilli(level, enhance);
            Assert.Equal(1000L * 1000L / success,
                CraftAssuranceHorizonReport.ExpectedAttemptsWithDowngradeMilli(level, enhance));
        }
    }

    [Fact]
    public void Downgrade_risk_exceeds_the_lower_bound_where_a_failure_falls_a_level()
    {
        var enhance = Enhance();
        var floor = enhance.DowngradeFromLevel;

        Assert.True(EnhancePolicy.BandFor(floor, enhance).CanDowngrade,
            "the shipped tuning changed: the first level at the downgrade floor is no longer in a downgrading band");

        var naive = 1000L * 1000L / EnhancePolicy.SuccessMilli(floor, enhance);
        var withDowngrade = CraftAssuranceHorizonReport.ExpectedAttemptsWithDowngradeMilli(floor, enhance);

        // Strictly greater: a fall has to be re-earned. The DIRECTION is the contract; the magnitude is
        // a reading (validation-ssot), so only the relation is asserted.
        Assert.True(withDowngrade > naive,
            $"the downgrade-aware expectation ({withDowngrade}) must exceed the lower bound ({naive})");
    }

    [Fact]
    public void Downgrade_risk_is_monotone_up_the_peril_band()
    {
        var enhance = Enhance();
        var previous = CraftAssuranceHorizonReport.ExpectedAttemptsWithDowngradeMilli(enhance.DowngradeFromLevel, enhance);

        for (var level = enhance.DowngradeFromLevel + 1; level <= enhance.DowngradeFromLevel + 10; level++)
        {
            var current = CraftAssuranceHorizonReport.ExpectedAttemptsWithDowngradeMilli(level, enhance);
            Assert.True(current >= previous, $"+" + level + " expects fewer attempts (" + current + ") than the level below (" + previous + ")");
            previous = current;
        }
    }

    [Fact]
    public void The_wear_pair_scales_linearly_with_the_rate_and_never_prices_gambling_below_certainty()
    {
        var enhance = Enhance();
        var assurance = Assurance();
        const int level = 20;

        var slow = CraftAssuranceHorizonReport.Row(level, enhance, assurance.AssureBonusMilli, 300, craftWearPerAttemptMilli: 40);
        var fast = CraftAssuranceHorizonReport.Row(level, enhance, assurance.AssureBonusMilli, 300, craftWearPerAttemptMilli: 80);

        Assert.NotNull(slow.ExpectedWearPerMilleOfMaxGambling);
        Assert.NotNull(slow.ExpectedWearPerMilleOfMaxCertainty);
        // The certainty route is ONE attempt at the rate, exactly.
        Assert.Equal(40L, slow.ExpectedWearPerMilleOfMaxCertainty);
        Assert.Equal(80L, fast.ExpectedWearPerMilleOfMaxCertainty);
        // The wear column is ceil(attempts × rate) in per-mille, so doubling the rate doubles the
        // expectation and the ROUNDING can differ by at most one unit -- the rate itself is rate-exact
        // (the certainty column above), and the attempts column is rate-independent.
        Assert.Equal(slow.ExpectedAttemptsWithDowngradeMilli, fast.ExpectedAttemptsWithDowngradeMilli);
        var doubled = 2 * slow.ExpectedWearPerMilleOfMaxGambling;
        Assert.True(fast.ExpectedWearPerMilleOfMaxGambling >= doubled - 1 && fast.ExpectedWearPerMilleOfMaxGambling <= doubled + 1,
            $"doubling the rate from 40 to 80 must roughly double the wear line: {slow.ExpectedWearPerMilleOfMaxGambling} -> {fast.ExpectedWearPerMilleOfMaxGambling}");
        Assert.True(slow.ExpectedWearPerMilleOfMaxGambling >= slow.ExpectedWearPerMilleOfMaxCertainty);

        // No rate supplied -> no wear columns, so every pre-existing caller's output is unchanged.
        var without = CraftAssuranceHorizonReport.Row(level, enhance, assurance.AssureBonusMilli, 300);
        Assert.Null(without.ExpectedWearPerMilleOfMaxGambling);
        Assert.Null(without.ExpectedWearPerMilleOfMaxCertainty);
    }

    [Fact]
    public void The_report_prints_the_downgrade_and_wear_columns_when_a_rate_is_given()
    {
        var corpus = DropVolumeCorpusTests.Corpus();
        var byId = corpus.Tables.ToDictionary(t => t.TableId, StringComparer.Ordinal);
        var assureId = MaterialCatalog.AssuranceId("assure");
        var yield = CraftAssuranceHorizonReport.ExpectedRefIdPerMille(
            byId["drop.web.wave-boss"], assureId, itemLevel: 10, id => byId.TryGetValue(id, out var t) ? t : null);

        var text = CraftAssuranceHorizonReport.Render(
            1, 20, Enhance(), Assurance().AssureBonusMilli, yield,
            CraftAssuranceHorizonReport.PerSourceYield(corpus.Sources, byId),
            craftWearPerAttemptMilli: 50);

        Assert.Contains("gambleAttemptsWithDowngradeMilli=", text, StringComparison.Ordinal);
        Assert.Contains("craftWearPerMilleOfMaxGambling=", text, StringComparison.Ordinal);
        Assert.Contains("craftWearPerMilleOfMaxCertainty=", text, StringComparison.Ordinal);
    }
}
