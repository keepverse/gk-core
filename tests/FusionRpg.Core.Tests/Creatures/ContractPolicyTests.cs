using FusionRpg.Core.Creatures;
using FusionRpg.Core.Creatures.Contracts;
using Xunit;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// spec-creature-contracts.md numbers block. Pure integer policy — every boundary that a store
/// transaction later leans on is pinned here, where it costs nothing to prove.
/// </summary>
public class ContractPolicyTests
{
    [Theory]
    [InlineData(0, LoyaltyRank.Insubordinate)]
    [InlineData(199, LoyaltyRank.Insubordinate)]
    [InlineData(200, LoyaltyRank.Bound)]
    [InlineData(399, LoyaltyRank.Bound)]
    [InlineData(400, LoyaltyRank.Sworn)]
    [InlineData(599, LoyaltyRank.Sworn)]
    [InlineData(600, LoyaltyRank.Trusted)]
    [InlineData(799, LoyaltyRank.Trusted)]
    [InlineData(800, LoyaltyRank.Devoted)]
    [InlineData(1000, LoyaltyRank.Devoted)]
    public void RankFor_walks_every_band_boundary(int loyalty, LoyaltyRank expected) =>
        Assert.Equal(expected, ContractPolicy.RankFor(loyalty));

    [Fact]
    public void Bound_pays_zero_milli_so_adopting_contracts_cannot_move_a_golden()
    {
        Assert.Equal(0, ContractPolicy.RankBonusMilli(LoyaltyRank.Insubordinate));
        Assert.Equal(0, ContractPolicy.RankBonusMilli(LoyaltyRank.Bound));
        Assert.Equal(15, ContractPolicy.RankBonusMilli(LoyaltyRank.Sworn));
        Assert.Equal(35, ContractPolicy.RankBonusMilli(LoyaltyRank.Trusted));
        Assert.Equal(60, ContractPolicy.RankBonusMilli(LoyaltyRank.Devoted));
        // A fresh contract must land in the zero-bonus band, or every battle hash moves.
        Assert.Equal(0, ContractPolicy.RankBonusMilli(ContractPolicy.RankFor(ContractPolicy.BindLoyalty)));
    }

    [Theory]
    [InlineData(199, false)]
    [InlineData(200, true)]
    public void IsDeployable_is_exactly_the_deploy_floor(int loyalty, bool expected) =>
        Assert.Equal(expected, ContractPolicy.IsDeployable(loyalty));

    [Fact]
    public void Personality_rate_table_matches_the_spec()
    {
        Assert.Equal(new PersonalityRates(120, 80, 100), ContractPolicy.Rates(CreaturePersonality.Loyal));
        Assert.Equal(new PersonalityRates(90, 60, 100), ContractPolicy.Rates(CreaturePersonality.Stoic));
        Assert.Equal(new PersonalityRates(100, 100, 130), ContractPolicy.Rates(CreaturePersonality.Proud));
        Assert.Equal(new PersonalityRates(100, 90, 110), ContractPolicy.Rates(CreaturePersonality.Calculating));
        Assert.Equal(new PersonalityRates(80, 150, 70), ContractPolicy.Rates(CreaturePersonality.Feral));
    }

    [Theory]
    [InlineData(CreatureRarity.Chaff, 2)]
    [InlineData(CreatureRarity.Cultivated, 5)]
    [InlineData(CreatureRarity.Heirloom, 12)]
    [InlineData(CreatureRarity.Sunwoven, 25)]
    public void Base_upkeep_is_rarity_scaled(CreatureRarity rarity, int expected) =>
        Assert.Equal(expected, ContractPolicy.BaseUpkeepPerDay(rarity));

    [Fact]
    public void Upkeep_applies_personality_percent_with_integer_truncation()
    {
        // feral legendary: 25 × 70 / 100 = 17.5 → 17. Truncation always favours the player.
        Assert.Equal(17, ContractPolicy.UpkeepPerDay(CreatureRarity.Sunwoven, CreaturePersonality.Feral));
        Assert.Equal(32, ContractPolicy.UpkeepPerDay(CreatureRarity.Sunwoven, CreaturePersonality.Proud));
        Assert.Equal(2, ContractPolicy.UpkeepPerDay(CreatureRarity.Chaff, CreaturePersonality.Loyal));
        // A cheap creature with a discount personality still costs something: never free, never negative.
        Assert.Equal(1, ContractPolicy.UpkeepPerDay(CreatureRarity.Chaff, CreaturePersonality.Feral));
    }

    [Fact]
    public void Decay_scales_with_personality_and_truncates()
    {
        Assert.Equal(15, ContractPolicy.DecayPerDayFor(CreaturePersonality.Stoic));   // 25 × 60 / 100
        Assert.Equal(25, ContractPolicy.DecayPerDayFor(CreaturePersonality.Proud));
        Assert.Equal(37, ContractPolicy.DecayPerDayFor(CreaturePersonality.Feral));   // 37.5 → 37
    }

    [Fact]
    public void Decay_never_crosses_the_deploy_floor()
    {
        Assert.Equal(275, ContractPolicy.ApplyDecay(300, CreaturePersonality.Proud));
        // 210 − 25 would be 185; the floor holds it at 200. Time strips what was earned, never access.
        Assert.Equal(200, ContractPolicy.ApplyDecay(210, CreaturePersonality.Proud));
        Assert.Equal(200, ContractPolicy.ApplyDecay(200, CreaturePersonality.Feral));
        // A creature already under the floor (defeats put it there) is not pushed further down by time.
        Assert.Equal(150, ContractPolicy.ApplyDecay(150, CreaturePersonality.Feral));
    }

    [Fact]
    public void Gains_respect_the_personality_rate_and_the_daily_cap()
    {
        // loyal: 15 × 120 / 100 = 18 credited, nothing banked yet.
        Assert.Equal((318, 18), ContractPolicy.ApplyGain(300, 0, ContractPolicy.WinGain, CreaturePersonality.Loyal));
        // Already 55 into the 60/day window: only 5 more lands.
        Assert.Equal((305, 60), ContractPolicy.ApplyGain(300, 55, ContractPolicy.WinGain, CreaturePersonality.Loyal));
        // Window exhausted: the win still happened, the loyalty did not move.
        Assert.Equal((300, 60), ContractPolicy.ApplyGain(300, 60, ContractPolicy.WinGain, CreaturePersonality.Loyal));
        // Ceiling clamps.
        Assert.Equal((1000, 18), ContractPolicy.ApplyGain(995, 0, ContractPolicy.WinGain, CreaturePersonality.Loyal));
    }

    [Fact]
    public void Losses_are_uncapped_and_may_cross_the_floor()
    {
        Assert.Equal(195, ContractPolicy.ApplyLoss(205));
        Assert.False(ContractPolicy.IsDeployable(ContractPolicy.ApplyLoss(205)));
        Assert.Equal(0, ContractPolicy.ApplyLoss(5));
    }

    [Fact]
    public void Ritual_gain_scales_and_price_is_rarity_only()
    {
        Assert.Equal(120, ContractPolicy.RitualGainFor(CreaturePersonality.Loyal));  // 100 × 120 / 100
        Assert.Equal(80, ContractPolicy.RitualGainFor(CreaturePersonality.Feral));
        Assert.Equal(50, ContractPolicy.RitualPrice(CreatureRarity.Chaff, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(100, ContractPolicy.RitualPrice(CreatureRarity.Cultivated, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(200, ContractPolicy.RitualPrice(CreatureRarity.Heirloom, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(400, ContractPolicy.RitualPrice(CreatureRarity.Sunwoven, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
    }

    [Fact]
    public void Slot_ladder_rises_forever_no_ceiling()
    {
        // T3.6 (spec-caps-reconcile.md §2.3, SSOT §11.1/§11.1a, 2026-08-24): MaxSlots is deleted.
        // The escalating price was always the real scarcity control -- SSOT §11.1a's own worked
        // table, independently re-derived here rather than copied: NextSlotPrice(purchased) =
        // 300 x (purchased+1), so the price to reach TOTAL slot N (purchased = N-12) is
        // 300 x (N-11), and the cumulative cost to reach N is a triangular sum.
        Assert.Equal(12, ContractPolicy.Capacity(0));
        Assert.Equal(300, ContractPolicy.NextSlotPrice(0, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(600, ContractPolicy.NextSlotPrice(1, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(900, ContractPolicy.NextSlotPrice(2, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));

        // Past the OLD 48-slot ceiling, capacity keeps climbing and buying never refuses.
        Assert.Equal(48, ContractPolicy.Capacity(36));
        Assert.True(ContractPolicy.CanBuySlot(35));
        Assert.True(ContractPolicy.CanBuySlot(36));   // used to be exactly the 48 ceiling -- not anymore
        Assert.Equal(49, ContractPolicy.Capacity(37)); // capacity keeps climbing past the old ceiling
        Assert.Equal(112, ContractPolicy.Capacity(100));
        Assert.True(ContractPolicy.CanBuySlot(10_000)); // no purchased count ever refuses

        // SSOT §11.1a's own table, exact: total slot 512 (purchased=500) prices at 150,300, cumulative
        // 37,575,000 -- the argument the whole deletion rests on, asserted precisely, not approximately.
        Assert.Equal(512, ContractPolicy.Capacity(500));
        Assert.Equal(150_300, ContractPolicy.NextSlotPrice(500, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));

        long cumulative = 0;
        for (var k = 0; k < 500; k++) cumulative += ContractPolicy.NextSlotPrice(k, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning);
        Assert.Equal(37_575_000, cumulative);
    }

    [Fact]
    public void Warden_property_holds_past_the_old_ceiling_it_never_depended_on()
    {
        // SSOT §11.1a: "the warden mechanic survives intact... because of the price formula, not
        // because of the cap." Proven directly: binding a warden still consumes a slot and the Nth
        // slot still costs strictly more than the (N-1)th, arbitrarily far past the old 48 ceiling.
        Assert.Equal(600_300, ContractPolicy.NextSlotPrice(2000, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning)); // the 2,012th total slot, SSOT's own row
        Assert.True(ContractPolicy.NextSlotPrice(2000, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning) > ContractPolicy.NextSlotPrice(1999, FusionRpg.Core.Creatures.SoulSinkPolicy.VanillaPvzTheta, FusionRpg.Core.Power.PowerTuningHub.Tuning));
        Assert.Equal(2012, ContractPolicy.Capacity(2000));
    }

    [Theory]
    // Day-quantised, not 24-hour windows: one minute past midnight UTC is a new tribute day.
    [InlineData("2026-08-21T23:59:00Z", "2026-08-22T00:01:00Z", 1)]
    [InlineData("2026-08-21T00:00:00Z", "2026-08-21T23:59:00Z", 0)]
    [InlineData("2026-08-31T12:00:00Z", "2026-09-02T01:00:00Z", 2)]   // month boundary
    [InlineData("2025-12-30T12:00:00Z", "2026-01-02T12:00:00Z", 3)]   // year boundary
    [InlineData("2024-02-28T12:00:00Z", "2024-03-01T12:00:00Z", 2)]   // leap day counts
    [InlineData("2026-01-01T00:00:00Z", "2026-12-01T00:00:00Z", 30)]  // long absence clamps
    [InlineData("2026-08-22T00:00:00Z", "2026-08-21T00:00:00Z", 0)]   // future stamp (SIM travel) never bills
    public void ElapsedDays_counts_whole_utc_days_and_clamps(string last, string now, int expected) =>
        Assert.Equal(expected, ContractPolicy.ElapsedDays(
            DateTimeOffset.Parse(last, null, System.Globalization.DateTimeStyles.AdjustToUniversal),
            DateTimeOffset.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal)));

    [Fact]
    public void Personality_is_derived_from_the_instance_id_and_never_changes()
    {
        var first = ContractPolicy.PersonalityFor("creature-abc-123");
        Assert.Equal(first, ContractPolicy.PersonalityFor("creature-abc-123"));
        // No distinctness assertion between two arbitrary ids: with five personalities, a collision
        // is the common case, not a defect. Determinism and full coverage are the real properties.

        // Every personality is reachable — a table nobody can roll is a dead table.
        var seen = new HashSet<CreaturePersonality>();
        for (var i = 0; i < 400; i++) seen.Add(ContractPolicy.PersonalityFor("instance-" + i));
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public void Personality_ids_round_trip()
    {
        foreach (CreaturePersonality p in Enum.GetValues(typeof(CreaturePersonality)))
        {
            Assert.True(CreaturePersonalityIds.TryParse(p.ToId(), out var parsed));
            Assert.Equal(p, parsed);
        }

        Assert.False(CreaturePersonalityIds.TryParse("smug", out _));
        Assert.False(CreaturePersonalityIds.TryParse(null, out _));
    }
}
