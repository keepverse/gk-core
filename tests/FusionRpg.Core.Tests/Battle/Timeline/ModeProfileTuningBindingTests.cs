using FusionRpg.Core.Battle;
using FusionRpg.Core.Battle.Timeline;
using Xunit;

namespace FusionRpg.Core.Tests.Battle.Timeline;

/// <summary>
/// battle-timeline T14/B29 (spec-timeline-tunables.md §2) — the three mode profiles' MAGNITUDES moved
/// to `data/tuning/battle.v{n}.json`'s `timeline.profiles`, with their STRUCTURE staying in code.
///
/// <para>Two things are proven here. First, binding: the value the catalog serves is the value config
/// carries, so a key that silently stops being read fails rather than reverting to a constant. Second,
/// refusal: config that is absent, contradictory, or degenerate is rejected at load — the module's
/// whole point is that there is no built-in default to fall back to.</para>
///
/// <para><b>Why the refusal cases drive the pure loader rather than reconfiguring the catalog.</b>
/// `BattleModeProfileCatalog.Configure` is global, and xUnit parallelises across test classes, so a
/// test that reconfigured it would race every other test that resolves a profile. `DerivedStatPolicy`
/// documents the same hazard and solved it with `AsyncLocal`; this module does not need that, because
/// the loader is pure and every refusal is a load-time decision.</para>
/// </summary>
public class ModeProfileTuningBindingTests
{
    static BattleTuning Configured => ContractTuningTestBootstrap.DefaultBattle;

    [Theory]
    [InlineData(BattleModeProfileCatalog.ClassicRoundId)]
    [InlineData(BattleModeProfileCatalog.GalaxySyncId)]
    [InlineData(BattleModeProfileCatalog.HybridAtbId)]
    [InlineData(BattleModeProfileCatalog.SiegeId)]
    public void EveryPublishedMagnitudeBinds(string profileId)
    {
        var profile = BattleModeProfileCatalog.Resolve(profileId);
        var tuned = Configured.ProfileOf(profileId);

        Assert.Equal(tuned.W, profile.W);
        Assert.Equal(tuned.WReact, profile.WReact);
        Assert.Equal(tuned.PassQuantum, profile.PassQuantum);
    }

    // ---- base-defense F2 / battle-clock-profile: MaxRounds/RoundDurationMs ----

    /// <summary>Decision 29 keeps `timeline.profiles.siege.maxRounds/roundDurationMs` unset in every
    /// shipped bootstrap, alongside the three pre-existing rows (which never named them at all). All
    /// four must inherit the ruleset horizon identically — the whole byte-identity argument for
    /// `classic-round` rests on "names neither, so it inherits both".</summary>
    [Theory]
    [InlineData(BattleModeProfileCatalog.ClassicRoundId)]
    [InlineData(BattleModeProfileCatalog.GalaxySyncId)]
    [InlineData(BattleModeProfileCatalog.HybridAtbId)]
    [InlineData(BattleModeProfileCatalog.SiegeId)]
    public void EveryShippedProfileInheritsTheRulesetHorizonWhenTuningNamesNeither(string profileId)
    {
        var tuned = Configured.ProfileOf(profileId);
        Assert.Null(tuned.MaxRounds);
        Assert.Null(tuned.RoundDurationMs);

        var profile = BattleModeProfileCatalog.Resolve(profileId);
        Assert.Equal(BattleRuleset.MaxRounds, profile.MaxRounds);
        Assert.Equal(BattleRuleset.RoundDurationMs, profile.RoundDurationMs);
    }

    [Fact]
    public void SiegeRowResolvesAndIsCached()
    {
        Assert.Same(BattleModeProfileCatalog.Siege, BattleModeProfileCatalog.Resolve(BattleModeProfileCatalog.SiegeId));
        Assert.Same(BattleModeProfileCatalog.Siege, BattleModeProfileCatalog.Siege);
    }

    /// <summary>§5.12 / decision "both sides move" — siege is speed-ordered and per-side-concurrent,
    /// which classic-round is neither.</summary>
    [Fact]
    public void SiegeIsSpeedOrderedPerSideAndInteractive()
    {
        var siege = BattleModeProfileCatalog.Siege;
        Assert.True(siege.OrdersBySpeed);
        Assert.Equal(WScope.PerSide, siege.WScope);
        Assert.True(siege.RequiresLiveInput);

        var classic = BattleModeProfileCatalog.ClassicRound;
        Assert.False(classic.OrdersBySpeed);
        Assert.False(classic.RequiresLiveInput);
    }

    /// <summary>battle-clock-profile §5's own correction, pinned as a regression test: the siege row
    /// must run `OneActionPerTurnEconomy`, never `ActionPointsEconomy` — action-map.md:430's "no
    /// Action Points... it is simply not what this mode needs". A points economy here was the exact
    /// wrong fix this module's own spec records having made once already.</summary>
    [Fact]
    public void SiegeRunsOneActionPerTurnNeverActionPoints()
    {
        var economy = BattleModeProfileCatalog.Siege.NewEconomy();
        Assert.IsType<OneActionPerTurnEconomy>(economy);
    }

    [Fact]
    public void UnknownProfileIdStillThrows()
    {
        Assert.Throws<ArgumentException>(() => BattleModeProfileCatalog.Resolve("sieg"));
    }

    /// <summary>A hand-constructed profile with a horizon that resolves to zero must fail loudly at
    /// `Build`, not silently produce a battle with no rounds — proven at the loader, matching every
    /// other refusal in this file.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeMaxRoundsIsRefused(int bad)
    {
        var ex = Assert.Throws<BattleTuningRejection>(() =>
            BattleTuningLoader.Parse(Doc(
                "\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1,\"maxRounds\":" + bad + "}")));
        Assert.Contains("maxRounds must be > 0", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AZeroOrNegativeRoundDurationIsRefused(int bad)
    {
        var ex = Assert.Throws<BattleTuningRejection>(() =>
            BattleTuningLoader.Parse(Doc(
                "\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1,\"roundDurationMs\":" + bad + "}")));
        Assert.Contains("roundDurationMs must be > 0", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveLoopGuardRoundMultipleIsRefused(int bad)
    {
        var doc =
            "{\"schemaVersion\":2,\"version\":2," +
            "\"ruleset\":{\"roundDurationMs\":1000,\"maxRounds\":50,\"loopGuardRoundMultiple\":" + bad + "}," +
            "\"statComposer\":{\"primaryAffinityDivisor\":4,\"secondaryAffinityDivisor\":8}," +
            "\"timeline\":{\"profiles\":{" + Classic + "}}," +
            "\"hybrid\":{\"secondaryWeightMilli\":0}," +
            "\"traits\":{}}";
        var ex = Assert.Throws<BattleTuningRejection>(() => BattleTuningLoader.Parse(doc));
        Assert.Contains("loopGuardRoundMultiple must be > 0", ex.Message);
    }

    /// <summary>A profile that DOES name its own horizon gets it, distinct from the ruleset — proven
    /// at the pure loader/tuning level (never through the global catalog, per this file's own
    /// documented parallel-safety discipline) so the resolution arithmetic
    /// (`t.MaxRounds ?? BattleRuleset.MaxRounds`) is exercised without racing other tests.</summary>
    [Fact]
    public void AProfileNamingItsOwnHorizonGetsIt()
    {
        var tuning = BattleTuningLoader.Parse(Doc(
            "\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1,\"maxRounds\":120,\"roundDurationMs\":500}"));
        var row = tuning.ProfileOf(BattleModeProfileCatalog.ClassicRoundId);
        Assert.Equal(120, row.MaxRounds);
        Assert.Equal(500, row.RoundDurationMs);

        // The resolution BattleModeProfileCatalog.Build performs, reproduced directly: named wins
        // over the ruleset fallback.
        Assert.Equal(120, row.MaxRounds ?? tuning.MaxRounds);
        Assert.Equal(500, row.RoundDurationMs ?? tuning.RoundDurationMs);
    }

    /// <summary>The belt-and-suspenders guard reproduces exactly 200,000 at classic-round's shipped
    /// 50 rounds (50 * 4000) — the refactor from a hard-coded constant to a profile-derived value must
    /// not be a behaviour change wearing a refactor's clothes.</summary>
    [Fact]
    public void LoopGuardReproducesTwoHundredThousandAtFiftyRounds()
    {
        Assert.Equal(50, BattleRuleset.MaxRounds);
        Assert.Equal(4000, BattleRuleset.LoopGuardRoundMultiple);
        checked
        {
            Assert.Equal(200_000, BattleRuleset.MaxRounds * BattleRuleset.LoopGuardRoundMultiple);
        }
    }

    /// <summary>`maxPoints` is the one magnitude that reaches an economy rather than the profile
    /// record, so binding it needs its own read — the budget an actor actually gets.</summary>
    [Fact]
    public void HybridAtbActionPointBudgetBindsToConfig()
    {
        var budget = Configured.ProfileOf(BattleModeProfileCatalog.HybridAtbId).MaxPoints;
        Assert.NotNull(budget);

        var economy = BattleModeProfileCatalog.HybridAtb.NewEconomy();
        // Spend exactly the configured budget, then prove the next acquire is refused: that is the
        // budget being real, not a field being echoed back.
        for (var i = 0; i < budget!.Value; i++)
            Assert.True(economy.TryAcquire("actor:a", 1, nowTick: 0), $"acquire {i + 1} of {budget} should fit the budget");
        Assert.False(economy.TryAcquire("actor:a", 1, nowTick: 0), "the budget must run out at exactly maxPoints");
    }

    /// <summary>Each profile stays a single instance — existing tests assert reference identity.</summary>
    [Fact]
    public void ProfilesAreCachedSingleInstances()
    {
        Assert.Same(BattleModeProfileCatalog.ClassicRound, BattleModeProfileCatalog.Resolve(null));
        Assert.Same(BattleModeProfileCatalog.ClassicRound, BattleModeProfileCatalog.ClassicRound);
        Assert.Same(BattleModeProfileCatalog.HybridAtb, BattleModeProfileCatalog.HybridAtb);
    }

    // ---- refusals, all via the pure loader ----

    // Plain concatenation, not an interpolated raw string: the JSON here is mostly braces, and
    // brace-counting against `$$"""` is a needless way to make a test fixture hard to read.
    static string Doc(string profiles) =>
        "{\"schemaVersion\":2,\"version\":2," +
        "\"ruleset\":{\"roundDurationMs\":1000,\"maxRounds\":50,\"loopGuardRoundMultiple\":4000}," +
        "\"statComposer\":{\"primaryAffinityDivisor\":4,\"secondaryAffinityDivisor\":8}," +
        "\"timeline\":{\"profiles\":{" + profiles + "}}," +
        // Wave E3 made `hybrid` a required section; 0 is the shipped, inert value.
        "\"hybrid\":{\"secondaryWeightMilli\":0}," +
        "\"speciesTempo\":{\"referenceIntervalMs\":1500}," +
        "\"traits\":{}}";

    const string Classic = "\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":1}";

    [Fact]
    public void AMissingTimelineSectionIsRefused()
    {
        var ex = Assert.Throws<BattleTuningRejection>(() => BattleTuningLoader.Parse("""
            {"schemaVersion":2,"version":2,
             "ruleset":{"roundDurationMs":1000,"maxRounds":50},
             "statComposer":{"primaryAffinityDivisor":4,"secondaryAffinityDivisor":8},
             "hybrid":{"secondaryWeightMilli":0},
             "traits":{}}
            """));
        Assert.Contains("timeline", ex.Message);
    }

    [Fact]
    public void AProfileTheCatalogShipsButConfigOmitsIsRefused_notDefaulted()
    {
        var tuning = BattleTuningLoader.Parse(Doc(Classic));
        var ex = Assert.Throws<BattleTuningRejection>(() => tuning.ProfileOf(BattleModeProfileCatalog.HybridAtbId));
        Assert.Contains("hybrid-atb", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveWIsRefused(int bad)
    {
        var ex = Assert.Throws<BattleTuningRejection>(() =>
            BattleTuningLoader.Parse(Doc("\"classic-round\":{\"w\":" + bad + ",\"wReact\":0,\"passQuantum\":1}")));
        Assert.Contains("w must be > 0", ex.Message);
    }

    /// <summary>A zero quantum makes a passing actor reschedule at `now` forever — an infinite loop
    /// that never advances the clock, which is the same failure `CooldownMath.MinTicksFloor` exists to
    /// prevent one layer down.</summary>
    [Fact]
    public void AZeroPassQuantumIsRefused()
    {
        var ex = Assert.Throws<BattleTuningRejection>(() =>
            BattleTuningLoader.Parse(Doc("\"classic-round\":{\"w\":1,\"wReact\":0,\"passQuantum\":0}")));
        Assert.Contains("passQuantum must be > 0", ex.Message);
    }
}
