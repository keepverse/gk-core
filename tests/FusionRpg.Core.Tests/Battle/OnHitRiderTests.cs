using FusionRpg.Core.Battle;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// combat-unification **Wave E1** — on-hit status riders.
///
/// <para>Riders live on <see cref="TraitBattleDef"/>, not on <c>BattleActorSetup</c>. That is a
/// measured choice, not a preference: the wave's spec offers both and then settles it ("Trait-sourced
/// riders come from `TraitBattleCatalog` rows"), and putting the list on `BattleActorSetup` was tried
/// first and **moved all four expedition tier goldens** — a new property lands inside the serialized
/// `BattleSetup` that `ExpeditionBattlePlan` hashes, so the hash moves for a purely structural reason
/// with no behaviour change. 35 battle goldens stayed green while only the expedition hash moved,
/// which is the signature of serialization-shape churn. A catalog row is not serialized.</para>
/// </summary>
public class OnHitRiderTests
{
    static string Doc(string traits) =>
        "{\"schemaVersion\":2,\"version\":2," +
        "\"ruleset\":{\"roundDurationMs\":1000,\"maxRounds\":50,\"loopGuardRoundMultiple\":4000}," +
        "\"statComposer\":{\"primaryAffinityDivisor\":4,\"secondaryAffinityDivisor\":8}," +
        "\"timeline\":{\"profiles\":{\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1}}}," +
        "\"hybrid\":{\"secondaryWeightMilli\":0}," +
        "\"speciesTempo\":{\"referenceIntervalMs\":1500}," +
        "\"traits\":{" + traits + "}}";

    // ---- inert by default: the wave's byte-identity invariant ----

    [Fact]
    public void NoShippedTraitCarriesARider()
    {
        Assert.All(TraitBattleCatalog.All, t =>
            Assert.True(t.OnHitRiders.Count == 0,
                $"trait '{t.TraitId}' ships a rider; Wave E1's zero-rider invariant assumes none do"));
    }

    [Fact]
    public void ATraitWithNoRidersArrayParsesToAnEmptyList()
    {
        var tuning = BattleTuningLoader.Parse(Doc("\"berserker\":{\"berserkRampHalfMilli\":250}"));
        Assert.Empty(tuning.TraitOf("berserker").OnHitRiders ?? Array.Empty<BattleStatusSpec>());
    }

    // ---- the authoring path ----

    [Fact]
    public void ARiderIsAuthoredWithTheSameGrammarAsAnInitialStatus()
    {
        var tuning = BattleTuningLoader.Parse(Doc(
            "\"berserker\":{\"onHitRiders\":[{\"statusId\":\"burn\",\"magnitudePerPulse\":-7," +
            "\"durationMs\":3000,\"periodMs\":500,\"grantChanceMilli\":250}]}"));

        var rider = Assert.Single(tuning.TraitOf("berserker").OnHitRiders!);
        Assert.Equal("burn", rider.StatusId);
        Assert.Equal(-7, rider.MagnitudePerPulse);
        Assert.Equal(3000, rider.DurationMs);
        Assert.Equal(500, rider.PeriodMs);
        Assert.Equal(250, rider.GrantChanceMilli);
    }

    /// <summary>Defaults match `BattleStatusSpec`'s own, so a rider is authored the way an initial
    /// status already is rather than needing its own conventions.</summary>
    [Fact]
    public void OmittedRiderFieldsTakeTheStatusSpecDefaults()
    {
        var tuning = BattleTuningLoader.Parse(Doc(
            "\"berserker\":{\"onHitRiders\":[{\"statusId\":\"burn\"}]}"));

        var rider = Assert.Single(tuning.TraitOf("berserker").OnHitRiders!);
        Assert.Equal(1000, rider.PeriodMs);
        Assert.Equal(1000, rider.GrantChanceMilli);
    }

    // ---- refusals ----

    [Theory]
    [InlineData("\"berserker\":{\"onHitRiders\":{}}")]                                   // not an array
    [InlineData("\"berserker\":{\"onHitRiders\":[3]}")]                                  // non-object entry
    [InlineData("\"berserker\":{\"onHitRiders\":[{\"magnitudePerPulse\":1}]}")]          // no statusId
    [InlineData("\"berserker\":{\"onHitRiders\":[{\"statusId\":\"b\",\"grantChanceMilli\":1001}]}")]
    [InlineData("\"berserker\":{\"onHitRiders\":[{\"statusId\":\"b\",\"grantChanceMilli\":-1}]}")]
    [InlineData("\"berserker\":{\"onHitRiders\":[{\"statusId\":\"b\",\"periodMs\":0}]}")]
    public void MalformedRiderContentIsRefused(string traits)
    {
        Assert.Throws<BattleTuningRejection>(() => BattleTuningLoader.Parse(Doc(traits)));
    }

    /// <summary>A chance is a probability, bounded 0..1000 by nature — refusing outside that is a
    /// structural bound, not a PS-8 progression cap.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public void TheChanceBoundsThemselvesAreLegal(int chance)
    {
        var tuning = BattleTuningLoader.Parse(Doc(
            "\"berserker\":{\"onHitRiders\":[{\"statusId\":\"b\",\"grantChanceMilli\":" + chance + "}]}"));
        Assert.Equal(chance, Assert.Single(tuning.TraitOf("berserker").OnHitRiders!).GrantChanceMilli);
    }
}
