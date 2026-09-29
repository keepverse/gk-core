using FusionRpg.Core.Battle;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// combat-unification **Wave E3**, live since **Phase 7 F1** (2026-09-07) — the secondary element as
/// a weighted payload component.
///
/// <para>Shipped inert at first on purpose: `hybrid.secondaryWeightMilli` was 0 from Wave E3
/// (2026-09-04) until Phase 7's owner decision raised it, so the payload was the single full-weight
/// primary component it had always been, and no golden moved. The weight is a <b>tunable</b> rather
/// than a hardcoded policy constant, because raising it is not free — it <b>moves the expedition
/// goldens</b>, since wave creatures carry a real `ElementSecondary` (`WaveCatalog.cs:115`) while the
/// hand-built battle goldens in this test project do not. The value now shipped, 300 (0.7/0.3), is the
/// exact number the original combat-unification map itself suggested.</para>
/// </summary>
public class HybridPayloadTests
{
    const ElementTypeId Fire = ElementTypeId.Fire;
    const ElementTypeId Ice = ElementTypeId.Ice;

    // ---- inert at the shipped default ----

    [Fact]
    public void AtWeightZeroThePayloadIsTheSingleFullWeightPrimary_evenWithASecondary()
    {
        var withSecondary = HybridPayload.Build(Fire, Ice, secondaryWeightMilli: 0);
        var without = HybridPayload.Build(Fire, null, secondaryWeightMilli: 0);

        var one = Assert.Single(withSecondary);
        Assert.Equal(Fire, one.Element);
        Assert.Equal(1.0, one.Weight);

        // Identical shape either way: a zero-weight second component must never reach the resolver.
        Assert.Equal(without.Length, withSecondary.Length);
    }

    [Fact]
    public void TheShippedTuningIsLiveAt300PerMille()
    {
        // Phase 7 F1 (2026-09-07, owner decision, creature-mechanism-gaps-ideal.md §2.6): raised off 0
        // -- gk-core/data/tuning/battle.v5.json and this test project's own ContractTuningTestBootstrap
        // fixture both carry 300 now, reusing the value the original combat-unification map itself
        // already suggested (0.7/0.3) rather than inventing a new one.
        Assert.Equal(300, BattleRuleset.HybridSecondaryWeightMilli);
    }

    [Fact]
    public void NoPrimaryMeansAnEmptyPayload()
    {
        Assert.Empty(HybridPayload.Build(null, Ice, 300));
        Assert.Empty(HybridPayload.Build(null, null, 0));
    }

    // ---- the mechanism, when the dial is on ----

    [Fact]
    public void ANonZeroWeightSplitsThePayloadAndTheWeightsSumToOne()
    {
        var payload = HybridPayload.Build(Fire, Ice, secondaryWeightMilli: 300);

        Assert.Equal(2, payload.Length);
        Assert.Equal(Fire, payload[0].Element);
        Assert.Equal(Ice, payload[1].Element);
        Assert.Equal(0.7, payload[0].Weight, precision: 10);
        Assert.Equal(0.3, payload[1].Weight, precision: 10);
        Assert.Equal(1.0, payload[0].Weight + payload[1].Weight, precision: 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(250)]
    [InlineData(500)]
    [InlineData(999)]
    [InlineData(1000)]
    public void TheWeightsAlwaysSumToExactlyOne(int weightMilli)
    {
        var payload = HybridPayload.Build(Fire, Ice, weightMilli);
        Assert.Equal(1.0, payload.Sum(c => c.Weight), precision: 10);
    }

    /// <summary>A secondary equal to the primary is not a hybrid. The engine normalises that one line
    /// before it calls this, so the payload builder is handed a null — asserted here so the two cannot
    /// drift into disagreeing about the rule.</summary>
    [Fact]
    public void ASecondaryIdenticalToThePrimaryIsNotAHybrid()
    {
        var payload = HybridPayload.Build(Fire, null, secondaryWeightMilli: 300);
        Assert.Single(payload);
        Assert.Equal(1.0, payload[0].Weight);
    }

    // ---- the bound is structural, and refuses ----

    [Theory]
    [InlineData(-1)]
    [InlineData(1001)]
    [InlineData(int.MaxValue)]
    public void AShareOutsideZeroToOneThousandIsRefused(int bad)
    {
        // Not a progression cap (PS-8): above 1000 the PRIMARY takes a negative weight, which is a
        // nonsense payload rather than an aggressive balance choice.
        Assert.Throws<ArgumentOutOfRangeException>(() => HybridPayload.Build(Fire, Ice, bad));
    }

    [Fact]
    public void TheLoaderRefusesAnOutOfRangeShareToo()
    {
        static string Doc(int w) =>
            "{\"schemaVersion\":2,\"version\":2," +
            "\"ruleset\":{\"roundDurationMs\":1000,\"maxRounds\":50,\"loopGuardRoundMultiple\":4000}," +
            "\"statComposer\":{\"primaryAffinityDivisor\":4,\"secondaryAffinityDivisor\":8}," +
            "\"timeline\":{\"profiles\":{\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1}}}," +
            "\"hybrid\":{\"secondaryWeightMilli\":" + w + "}," +
            "\"speciesTempo\":{\"referenceIntervalMs\":1500}," +
            "\"traits\":{}}";

        Assert.Throws<BattleTuningRejection>(() => BattleTuningLoader.Parse(Doc(1001)));
        Assert.Throws<BattleTuningRejection>(() => BattleTuningLoader.Parse(Doc(-1)));

        var ok = BattleTuningLoader.Parse(Doc(300));
        Assert.Equal(300, ok.HybridSecondaryWeightMilli);
    }
}
