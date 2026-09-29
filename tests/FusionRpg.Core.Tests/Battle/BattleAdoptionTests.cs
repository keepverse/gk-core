using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Battle;

/// <summary>
/// U10 — the re-tune rate tests (owner decision 5). COMPUTED, not sampled: the sigmoid is a
/// pure function, so the acceptance criteria are exact probability assertions.
/// </summary>
public class BattleRateTests
{
    static double HitAtParity(int level) => CombatProbability.Sigmoid(
        BattleRuleset.BaseAccuracy(level) - BattleRuleset.BaseDodge(level),
        CombatProbabilityPolicy.AccuracyScale);

    static double CritAtParity(int level) => CombatProbability.Sigmoid(
        BattleRuleset.BaseCritRate(level) - BattleRuleset.BaseCritResist(level),
        CombatProbabilityPolicy.CritRateScale);

    // battle-rates (T2.2): extended past the original 1/5/10/20 to prove parity invariance holds at
    // Theta = 10,000 too — spec-battle-rates.md §5, "not just 1/5/10/20". Same test, wider range;
    // per the spec's own boundary, extend rather than duplicate this into a separate file.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void Parity_hit_rate_is_ninety_percent(int level) =>
        Assert.InRange(HitAtParity(level), 0.88, 0.92);

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(20)]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(10000)]
    public void Parity_crit_rate_is_five_to_ten_percent(int level) =>
        Assert.InRange(CritAtParity(level), 0.05, 0.10);

    [Fact]
    public void Overleveled_attacker_hits_at_least_ninety_seven_percent()
    {
        // Growth target: +5 levels of accuracy over the defender's dodge.
        var p = CombatProbability.Sigmoid(
            BattleRuleset.BaseAccuracy(10) - BattleRuleset.BaseDodge(5),
            CombatProbabilityPolicy.AccuracyScale);
        Assert.True(p >= 0.97, $"expected ≥0.97, got {p:F4}");
    }

    [Fact]
    public void Underleveled_attacker_still_lands_most_hits()
    {
        // −5 levels: σ(2.2 − 1.3) = σ(0.9) ≈ 0.71 — outleveled but not helpless.
        var p = CombatProbability.Sigmoid(
            BattleRuleset.BaseAccuracy(5) - BattleRuleset.BaseDodge(10),
            CombatProbabilityPolicy.AccuracyScale);
        Assert.InRange(p, 0.60, 0.80);
    }

    [Fact]
    public void Critical_hunter_recost_lands_near_twenty_seven_percent()
    {
        // Reads the MIGRATED source since E12 — the magnitude is a row now, not a catalog field.
        // The outcome this asserts is unchanged, which is the whole point of the migration.
        var mod = TraitAtomSource.Shipped().ModsFor("critical-hunter")
            .Single(m => m.ChannelId == DerivedStatChannels.CombatCritRateOmni);
        var p = CombatProbability.Sigmoid(
            BattleRuleset.BaseCritRate(5) + mod.Amount - BattleRuleset.BaseCritResist(5),
            CombatProbabilityPolicy.CritRateScale);
        Assert.InRange(p, 0.20, 0.33);
    }
}

/// <summary>U11 — parity: a battle swing must equal a direct resolver call, number for number.</summary>
public class BattleResolverParityTests
{
    static BattleActorSetup Actor(string key, string side, int level = 5, ElementTypeId? elem = null) => new()
    {
        Key = key, Side = side, SpeciesId = "spec", TypeId = 1, Level = level,
        ElementPrimary = elem,
        MaxHp = BattleRuleset.BaseHp(level), Atk = BattleRuleset.BaseAtk(level),
        Defense = BattleRuleset.BaseDefense(level)
    };

    [Fact]
    public void First_swing_matches_a_direct_resolver_call_on_the_same_stream()
    {
        // 1-v-1, seed fixed. Replay the engine's exact stream consumption for round 1's
        // first swing: initiative draws happen on their own stream, so the crit stream's
        // first draws belong to the first attack.
        // 1 HP each: whoever swings first ends it, so the winner's tally is exactly one swing.
        var squad = Actor("squad:0", "squad", elem: ElementTypeId.Fire) with { MaxHp = 1 };
        var wave = Actor("wave:0", "wave", elem: ElementTypeId.Ice) with { MaxHp = 1 };
        const ulong seed = 77;

        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "parity", Squad = new[] { squad }, Wave = new[] { wave }
        }, seed);

        // Determine round-1 initiative order exactly as the engine does.
        var initiative = SeededRng.DeriveStream(seed, "initiative");
        var squadRoll = initiative.NextInt(1000);
        var waveRoll = initiative.NextInt(1000);
        var first = squadRoll <= waveRoll ? squad : wave;   // OrderBy is stable on ties
        var second = ReferenceEquals(first, squad) ? wave : squad;

        var calc = new OverlayCombatCalculator();
        var rng = new SeededRngCombatAdapter(SeededRng.DeriveStream(seed, "crit"));
        var (delta, breakdown) = calc.Compute(new OverlayCombatRequest
        {
            BaseOverlayDamage = first.Atk,
            Components = first.ElementPrimary is { } e
                ? new[] { new ElementPayloadComponent(e, 1.0) }
                : Array.Empty<ElementPayloadComponent>(),
            Attacker = new CombatActorSnapshot(BattleHubCompose.Compose(first),
                ActorElementTypes.Create(first.ElementPrimary)),
            Defender = new CombatActorSnapshot(BattleHubCompose.Compose(second),
                ActorElementTypes.Create(second.ElementPrimary)),
            Profile = CombatProfile.BattleSim
        }, rng);

        // No branching: this is a deterministic parity lock, so it asserts the NUMBER. The
        // defender holds 1 HP, so the first landed swing ends the battle and the winner's
        // tally IS that one swing — nothing to disentangle.
        Assert.True(breakdown.Hit,
            "seed 77's first swing must land for this parity lock — re-pick the seed if the ruleset moves");

        var attackerResult = report.Actors.Single(a => a.Key == first.Key);
        var defenderResult = report.Actors.Single(a => a.Key == second.Key);
        Assert.Equal((int)(-delta), attackerResult.DamageDealt);
        Assert.False(defenderResult.Survived);
        Assert.Equal(1, report.Rounds);
    }

    [Fact]
    public void Chip_floor_prevents_zero_damage_stalemates()
    {
        // Max-defense wall: without the chip floor this was a deterministic 0-damage
        // stalemate (audit F2). With it, the wall grinds down and the battle RESOLVES.
        // Sizing: L5 Atk 32 → chip = ceil(1.6) = 2/landed hit at ~90% hit → ~45 landed in
        // 50 rounds → a 60 HP wall falls near round 34. Tank Atk 1 so it can't win first.
        var tank = Actor("wave:0", "wave", level: 1) with
        {
            MaxHp = 60,
            Atk = 1,
            ChannelMods = new[] { new BattleChannelMod(DerivedStatChannels.CombatDefenseOmni, 500) }
        };
        var report = BattleEngine.Resolve(new BattleSetup
        {
            WaveId = "chip-grind",
            Squad = new[] { Actor("squad:0", "squad", level: 5) },
            Wave = new[] { tank }
        }, 3);
        Assert.Equal(BattleOutcome.Victory, report.Outcome);   // grinds through, never stalls
    }

    [Fact]
    public void Retired_symbols_stay_retired()
    {
        // The self-arming ban test (CombatSsotContractTests) now runs its armed branch —
        // this is the local assertion that arming actually happened.
        // battle-hub-fuse (2026-09-13): RulesetVersion 4 -> 5.
        Assert.Equal(5, BattleRuleset.RulesetVersion);
    }
}

