using System.Text.Json;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Mutation;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `enhance-reroll` (item module 15) against the REAL shipped tuning —
/// `gk-core/data/tuning/enhancement.v1.json`, `gk-core/data/tuning/item-rarity.v1.json` (module 7's seeded
/// `enhance_cap` column) and `gk-core/data/tuning/power-scale.v2.json`. Nothing here is synthetic except the
/// deliberately-broken documents in the loader-refusal tests.
/// </summary>
public class EnhancePolicyTests
{
    internal static string TuningJson() =>
        File.ReadAllText(Path.Combine(MaterialCorpusTests.RepoRoot(), "data", "tuning", "enhancement.v1.json"));

    internal static EnhancementTuning Tuning() => EnhancementTuning.Parse(TuningJson());

    internal static IReadOnlyDictionary<string, ItemRarityRungTuning> RarityTuning() =>
        ItemRarityTuning.Parse(File.ReadAllText(
            Path.Combine(MaterialCorpusTests.RepoRoot(), "data", "tuning", "item-rarity.v1.json")));

    /// <summary>D4's shipped content reach. A test fixture, not a production dial.</summary>
    const int V1ItemLevel = 32;

    internal static PowerTuning Power() => PowerTuningLoader.Parse(File.ReadAllText(
        Path.Combine(MaterialCorpusTests.RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    // ---- the tuning file ---------------------------------------------------------------------------

    [Fact]
    public void The_cost_and_odds_curves_are_read_from_data_tuning()
    {
        // AGENTS.md's balance-surface rule, mechanically: every number the policy uses arrives from
        // the file, so a balance pass is a file save. Asserted by parsing the REAL file and finding
        // each dial on it.
        var t = Tuning();
        Assert.Equal(20, t.ScalarPerLevelMilli);
        Assert.Equal(8, t.AsymptoteK);
        Assert.Equal(700, t.TransferRatioMilli);
        Assert.Equal(8, t.TransferItemLevelWindow);
        Assert.Equal(3, t.Bands.Count);
        Assert.Equal(new[] { "safe", "risk", "peril" }, t.Bands.Select(b => b.Id).ToArray());
    }

    [Fact]
    public void The_top_band_is_open_ended_so_there_is_no_hard_cap_on_plus_x()
    {
        var t = Tuning();
        Assert.Null(t.Bands[^1].ToLevel);
        // And a closed top band is refused at LOAD, not discovered at +21.
        var closed = TuningJson().Replace("\"toLevel\": null,  \"successStartMilli\": 500", "\"toLevel\": 20,  \"successStartMilli\": 500");
        Assert.NotEqual(TuningJson(), closed);
        var ex = Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(closed));
        Assert.Contains("hard stop", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_zero_success_floor_is_refused_at_load_because_it_is_the_luck_wall_D7_forbids()
    {
        var broken = TuningJson().Replace("\"successEndMilli\": 200,  \"spanLevels\": 10", "\"successEndMilli\": 0,  \"spanLevels\": 10");
        Assert.NotEqual(TuningJson(), broken);
        var ex = Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(broken));
        Assert.Contains("D7", ex.Message);
    }

    [Fact]
    public void A_lossless_transfer_ratio_is_refused_at_load()
    {
        var broken = TuningJson().Replace("\"transferRatioMilli\": 700", "\"transferRatioMilli\": 1000");
        var ex = Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(broken));
        Assert.Contains("portable currency", ex.Message);
    }

    [Fact]
    public void A_rung_dominant_reroll_price_is_refused_at_load()
    {
        // ssot-rarity.md §9.7/§8.1: the price must scale with AFFIX COUNT, not rung alone. A tuning
        // where the rung leg out-spreads the affix leg inverts the "low rungs are the best crafting
        // bases" mechanism, so it fails at boot rather than in a balance review nobody runs.
        var broken = TuningJson().Replace("\"rerollCostRungSlopeMilli\": 220", "\"rerollCostRungSlopeMilli\": 2000");
        var ex = Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(broken));
        Assert.Contains("affix count", ex.Message);
    }

    [Fact]
    public void A_gap_between_bands_is_refused_because_it_is_a_level_with_no_odds()
    {
        var broken = TuningJson().Replace("\"fromLevel\": 9", "\"fromLevel\": 10");
        Assert.Throws<EnhancementTuningRejection>(() => EnhancementTuning.Parse(broken));
    }

    // ---- the bands ---------------------------------------------------------------------------------

    [Fact]
    public void The_three_bands_carry_the_specs_own_odds()
    {
        var t = Tuning();
        Assert.Equal(1000, EnhancePolicy.SuccessMilli(1, t));
        Assert.Equal(1000, EnhancePolicy.SuccessMilli(8, t));
        Assert.Equal(950, EnhancePolicy.SuccessMilli(9, t));
        Assert.Equal(600, EnhancePolicy.SuccessMilli(14, t));
        Assert.Equal(500, EnhancePolicy.SuccessMilli(15, t));
        Assert.Equal(200, EnhancePolicy.SuccessMilli(25, t));
    }

    [Fact]
    public void The_success_curve_never_reaches_zero_at_any_level()
    {
        // D7 — "cost, never luck". The peril band holds at its floor forever rather than decaying to
        // an unwinnable roll, and the floor is a tunable the loader refuses to let reach zero.
        var t = Tuning();
        for (var level = 1; level <= 5000; level++)
            Assert.True(EnhancePolicy.SuccessMilli(level, t) > 0, $"+{level} has a zero success chance");
    }

    [Fact]
    public void The_success_curve_is_monotone_non_increasing()
    {
        var t = Tuning();
        for (var level = 2; level <= 500; level++)
            Assert.True(EnhancePolicy.SuccessMilli(level, t) <= EnhancePolicy.SuccessMilli(level - 1, t));
    }

    // ---- the gain curve, §4a -----------------------------------------------------------------------

    [Fact]
    public void No_enhancement_gain_is_a_hard_stop()
    {
        // ⭐ Replaces module 7's `no_enhancement_cap_is_a_hard_stop`. For EVERY rung and EVERY n,
        // gain(n+1) > gain(n): the asymptote is never reached, so no level is ever refused. Compared
        // exactly (cross-multiplied longs), not through a per-mille render that can tie at high n.
        var t = Tuning();
        foreach (var (rung, rowTuning) in RarityTuning())
            for (var n = 0; n < 4096; n++)
                Assert.True(EnhancePolicy.GainIsStrictlyIncreasing(n, n + 1, rowTuning.EnhanceCapMilli, t),
                    $"{rung}: gain(+{n + 1}) is not above gain(+{n}) — that is a hard stop");
    }

    [Fact]
    public void Enhancement_gain_stays_below_its_rungs_asymptote_at_every_n()
    {
        // §4a — the ladder cannot invert, read from module 7's SEEDED enhance_cap column rather than
        // a local constant. The pair with `Enhance_cap_asymptotes_below_one_rung_step_at_every_rung`
        // (module 7, ItemRarityTuningTests) is cross-referenced in both specs: if either moves the
        // other goes red.
        var t = Tuning();
        foreach (var (rung, rowTuning) in RarityTuning())
            for (var n = 1; n <= 10_000; n *= 2)
                Assert.True(EnhancePolicy.GainMilli(n, rowTuning.EnhanceCapMilli, t) < rowTuning.EnhanceCapMilli,
                    $"{rung}: gain at +{n} reached its own asymptote");
    }

    [Fact]
    public void The_shipped_asymptote_reproduces_the_specs_worked_numbers()
    {
        // §4a: "the asymptotic curve at the same +12 with K = 8 yields 120‰ on an almanac".
        var t = Tuning();
        var almanac = RarityTuning()["almanac"].EnhanceCapMilli;
        Assert.Equal(200, almanac);
        Assert.Equal(120, EnhancePolicy.GainMilli(12, almanac, t));
        // And I6's naive linear track at the same level is 240‰ — already past firstseed's 232‰ and
        // both 200‰ rows, which is the reason §4a replaced its shape.
        Assert.Equal(240, EnhancePolicy.LinearGainMilli(12, t));
    }

    [Fact]
    public void Every_scaled_magnitude_is_long_and_overflow_throws()
    {
        var t = Tuning();
        // A +129 t5 affix at ilvl 500 is not an int, and the API says so in its own signature.
        var big = 3_000_000_000L;
        Assert.True(EnhancePolicy.ScaledValue(big, 129, 860, t) > big);
        Assert.Throws<OverflowException>(() => EnhancePolicy.ScaledValue(long.MaxValue / 2, 4000, 860, t));
    }

    // ---- the item-level relation -------------------------------------------------------------------

    [Fact]
    public void The_item_level_cap_is_a_floor_with_no_ceiling()
    {
        var t = Tuning();
        Assert.Equal(4, EnhancePolicy.MaxLevelForItemLevel(0, t));
        Assert.Equal(12, EnhancePolicy.MaxLevelForItemLevel(32, t));
        Assert.Equal(36, EnhancePolicy.MaxLevelForItemLevel(128, t));
        Assert.Equal(129, EnhancePolicy.MaxLevelForItemLevel(500, t));
        // No ceiling: it keeps climbing for as long as item level does.
        Assert.True(EnhancePolicy.MaxLevelForItemLevel(1_000_000, t) > EnhancePolicy.MaxLevelForItemLevel(999_999, t) - 1);
    }

    // ---- outcomes ----------------------------------------------------------------------------------

    [Fact]
    public void There_is_no_destroy_outcome_in_the_enum_or_the_reason_codes()
    {
        // Asserted directly — a code nothing emits is a lie in a table, and reserving one invites a
        // later session to wire it up.
        foreach (var name in Enum.GetNames<EnhanceOutcome>())
            Assert.DoesNotContain("destroy", name, StringComparison.OrdinalIgnoreCase);
        foreach (var name in Enum.GetNames<AtomRejectionReason>())
            Assert.DoesNotContain("destroy", name, StringComparison.OrdinalIgnoreCase);
        foreach (var name in Enum.GetNames<MutationOpKind>())
            Assert.DoesNotContain("destroy", name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_safe_band_attempt_always_succeeds_and_a_peril_failure_can_drop_one_level()
    {
        var t = Tuning();
        for (var seed = 0UL; seed < 64; seed++)
        {
            var safe = EnhancePolicy.Resolve(new EnhanceContext(9, 200, 3, 0, false),
                SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t, out var refusal);
            Assert.True(refusal.IsOk);
            Assert.Equal(EnhanceOutcome.Success, safe.Outcome);
            Assert.Equal(4, safe.LevelAfter);
        }

        var sawDowngrade = false;
        for (var seed = 0UL; seed < 256 && !sawDowngrade; seed++)
        {
            var peril = EnhancePolicy.Resolve(new EnhanceContext(9, 200, 20, 0, false),
                SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t, out _);
            if (peril.Outcome != EnhanceOutcome.FailureWithDowngrade) continue;
            sawDowngrade = true;
            Assert.Equal(19, peril.LevelAfter);
            Assert.Equal(1, peril.PityCounterAfter);
        }

        Assert.True(sawDowngrade, "no peril failure downgraded in 256 seeds — the downgrade leg is unreachable");
    }

    [Fact]
    public void A_ward_suppresses_the_downgrade_but_never_the_failure()
    {
        var t = Tuning();
        var sawWardedFailure = false;
        for (var seed = 0UL; seed < 256; seed++)
        {
            var warded = EnhancePolicy.Resolve(new EnhanceContext(9, 200, 20, 0, WardLoaded: true),
                SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t, out _);
            Assert.NotEqual(EnhanceOutcome.FailureWithDowngrade, warded.Outcome);
            if (warded.Outcome == EnhanceOutcome.Failure)
            {
                sawWardedFailure = true;
                Assert.Equal(20, warded.LevelAfter);
            }
        }

        Assert.True(sawWardedFailure);
    }

    [Fact]
    public void An_attempt_past_the_items_own_level_cap_is_refused_by_name()
    {
        var t = Tuning();
        var attempt = EnhancePolicy.Resolve(new EnhanceContext(9, 32, 12, 0, false),
            SeededRng.DeriveStream(1, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t, out var refusal);
        Assert.Equal(AtomRejectionReason.ContentRuleViolated, refusal.Reason);
        Assert.Contains("enhance.item-level-cap", refusal.Detail);
        Assert.Contains("not a progression ceiling", refusal.Detail);
        Assert.Equal(12, attempt.LevelAfter);
    }

    [Fact]
    public void No_cost_or_odds_input_reads_a_player_property()
    {
        // D26, the same guard shape module 14 used: there is nowhere in the context type to PUT a
        // player property, and that is the enforcement.
        var forbidden = new[] { "player", "account", "theta", "perday", "daily", "power", "streak" };
        foreach (var property in typeof(EnhanceContext).GetProperties())
            foreach (var word in forbidden)
                Assert.False(property.Name.Contains(word, StringComparison.OrdinalIgnoreCase),
                    $"EnhanceContext.{property.Name} looks like a player property — D26 forbids one here");
    }

    // ---- species-gear-chain T41: assure loads the effective success before the roll -----------------

    [Fact]
    public void A_load_that_lands_exactly_on_certainty_always_succeeds()
    {
        var t = Tuning();
        const int currentLevel = 19; // target +20, the same "peril" level the ward tests already use
        var baseSuccess = EnhancePolicy.SuccessMilli(currentLevel + 1, t);
        var neededBonus = 1000 - baseSuccess;
        Assert.True(neededBonus > 0, "the peril band must not already be certain, or this test proves nothing");

        for (var seed = 0UL; seed < 64; seed++)
        {
            var attempt = EnhancePolicy.Resolve(
                new EnhanceContext(9, 200, currentLevel, 0, false, AssureLoaded: 1),
                SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t,
                out var refusal, assureBonusMilli: neededBonus);
            Assert.True(refusal.IsOk, refusal.Detail);
            Assert.Equal(1000, attempt.SuccessMilli);
            Assert.Equal(EnhanceOutcome.Success, attempt.Outcome);
            Assert.Equal(currentLevel + 1, attempt.LevelAfter);
        }
    }

    [Fact]
    public void A_load_past_certainty_refuses_by_name_and_consumes_nothing()
    {
        var t = Tuning();
        const int currentLevel = 19;
        var baseSuccess = EnhancePolicy.SuccessMilli(currentLevel + 1, t);
        var oneTooMuch = 1000 - baseSuccess + 1;

        var attempt = EnhancePolicy.Resolve(
            new EnhanceContext(9, 200, currentLevel, 0, false, AssureLoaded: 1),
            SeededRng.DeriveStream(1, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t,
            out var refusal, assureBonusMilli: oneTooMuch);

        Assert.Equal(AtomRejectionReason.ContentRuleViolated, refusal.Reason);
        Assert.Contains("enhance.assure-overloaded", refusal.Detail);
        Assert.Contains("past certainty", refusal.Detail);
        // Consumes nothing: the level and pity counter are byte-identical to the input.
        Assert.Equal(currentLevel, attempt.LevelAfter);
        Assert.Equal(0, attempt.PityCounterAfter);
    }

    [Fact]
    public void Zero_loaded_charges_is_byte_identical_to_no_assure_at_all()
    {
        var t = Tuning();
        for (var seed = 0UL; seed < 16; seed++)
        {
            var stream = () => SeededRng.DeriveStream(seed, MutationOpKinds.StreamName(MutationOpKind.Enhance));
            var withoutAssure = EnhancePolicy.Resolve(new EnhanceContext(9, 200, 3, 0, false), stream(), t, out var r1);
            var withZeroLoad = EnhancePolicy.Resolve(
                new EnhanceContext(9, 200, 3, 0, false, AssureLoaded: 0), stream(), t, out var r2,
                assureBonusMilli: 500);
            Assert.Equal(withoutAssure, withZeroLoad);
            Assert.Equal(r1.IsOk, r2.IsOk);
        }
    }

    [Fact]
    public void A_negative_assure_load_throws()
    {
        var t = Tuning();
        Assert.Throws<ArgumentOutOfRangeException>(() => EnhancePolicy.Resolve(
            new EnhanceContext(9, 200, 3, 0, false, AssureLoaded: -1),
            SeededRng.DeriveStream(1, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t, out _));
    }

    [Fact]
    public void Assure_load_and_bonus_widen_before_multiplying_and_never_wrap()
    {
        // The overload gate fires long before int overflow could — this proves the multiply itself
        // is `long`, not that a real load could ever reach these magnitudes.
        var t = Tuning();
        var attempt = EnhancePolicy.Resolve(
            new EnhanceContext(9, 200, 3, 0, false, AssureLoaded: int.MaxValue),
            SeededRng.DeriveStream(1, MutationOpKinds.StreamName(MutationOpKind.Enhance)), t,
            out var refusal, assureBonusMilli: 1000);
        Assert.Contains("enhance.assure-overloaded", refusal.Detail);
    }

    [Fact]
    public void Milestones_are_a_stride_and_never_stop()
    {
        var t = Tuning();
        Assert.Equal(4, t.MilestoneStride);
        foreach (var level in new[] { 4, 8, 12, 16, 20 })
            Assert.True(EnhancePolicy.IsMilestoneLevel(level, t));
        Assert.False(EnhancePolicy.IsMilestoneLevel(21, t));
        // ⭐ The point of a stride over a five-entry list: +24 is still a milestone.
        Assert.True(EnhancePolicy.IsMilestoneLevel(24, t));
        Assert.True(EnhancePolicy.IsMilestoneLevel(400, t));
    }

    // ---- §4b, the crafting horizon -----------------------------------------------------------------

    [Fact]
    public void The_crafting_horizon_is_computed_from_power_tuning_not_authored()
    {
        // §4b's headline: N ≈ 0.19 realms at v1's shipped reach (Θc 20, ilvl 32, +12, ×1.24), with
        // Θ′ = 24.67 — reproduced from the SHIPPED power-scale.v2.json, not from a number in a doc.
        var t = Tuning();
        // ilvl 32 is D4's shipped content reach, passed in rather than hardcoded in the report —
        // Θc comes from the power curve's own pinIndex, so the two cannot drift apart.
        var row = CraftingHorizonReport.V1Reach(t, Power(), V1ItemLevel);

        Assert.Equal(12, row.MaxLevel);
        Assert.Equal(240, row.GainMilli);
        Assert.Equal(24_670, row.ThetaPrimeMilli);
        Assert.Equal(186, row.RealmsMilli); // 0.186 → "about a fifth of a realm"
    }

    [Fact]
    public void The_horizon_reproduces_the_specs_whole_table()
    {
        var t = Tuning();
        var power = Power();

        // (Θc, ilvl, +cap, gain‰, Θ′×1000, N×1000) — §4b's own table, every row.
        var expected = new (int Theta, int Ilvl, int Cap, long Gain, long ThetaPrime, long Realms)[]
        {
            (20, 32, 12, 240, 24_670, 186),
            (20, 20, 9, 180, 23_525, 141),
            (50, 50, 16, 320, 62_407, 496),
            (100, 100, 29, 580, 136_982, 1_479),
            (123, 123, 34, 680, 173_278, 2_011),
            (200, 200, 54, 1080, 311_745, 4_469),
            (500, 500, 129, 2580, 999_404, 19_976),
        };

        foreach (var e in expected)
        {
            var row = CraftingHorizonReport.LinearRow(e.Theta, e.Ilvl, t, power);
            Assert.Equal(e.Cap, row.MaxLevel);
            Assert.Equal(e.Gain, row.GainMilli);
            Assert.Equal(e.ThetaPrime, row.ThetaPrimeMilli);
            Assert.Equal(e.Realms, row.RealmsMilli);
        }
    }

    [Fact]
    public void The_first_depth_worth_two_realms_is_computed_not_asserted()
    {
        // §2h.3's threshold is 2 realms and it lands at Θc ≈ 123 — five realms deep into a ladder
        // that stops at level 10 today. Searched over the real curve, so it moves when bMilli does.
        Assert.Equal(123, CraftingHorizonReport.FirstThetaReachingRealms(2000, Tuning(), Power()));
    }

    [Fact]
    public void The_soft_cap_makes_the_horizon_smaller_and_that_is_the_intended_direction()
    {
        // §4a's asymptote at v1's reachable +12 on an almanac is ×1.12 → N = 0.09, and the asymptote
        // itself caps at ×1.20 → N ≤ 0.16 at ANY n. Deliberate: the alternative is a gain that
        // inverts the rarity ladder.
        var t = Tuning();
        var power = Power();
        var cap = RarityTuning()["almanac"].EnhanceCapMilli;

        var capped = CraftingHorizonReport.CappedRow(20, 32, cap, t, power);
        Assert.Equal(120, capped.GainMilli);
        Assert.Equal(94, capped.RealmsMilli);

        var asymptote = CraftingHorizonReport.AsymptoteRow(20, 32, cap, power);
        Assert.Equal(156, asymptote.RealmsMilli);
        Assert.True(asymptote.RealmsMilli < 160, "the almanac asymptote must stay under N = 0.16");
        Assert.True(capped.RealmsMilli < CraftingHorizonReport.V1Reach(t, power, V1ItemLevel).RealmsMilli);
    }

    [Fact]
    public void The_horizon_moves_when_the_power_dial_moves()
    {
        // "The figure is in the report, not only in this document" — flattening contentScale (PS-7's
        // bMilli dial, which is NOT this program's to turn) changes N, which is the property that
        // makes the report worth shipping.
        var t = Tuning();
        var shipped = Power();
        var raw = File.ReadAllText(Path.Combine(MaterialCorpusTests.RepoRoot(), "data", "tuning", "power-scale.v2.json"));
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal(400, doc.RootElement.GetProperty("curve").GetProperty("bMilli").GetInt64());

        var flatter = PowerTuningLoader.Parse(raw.Replace("\"bMilli\": 400", "\"bMilli\": 200"));
        Assert.NotEqual(
            CraftingHorizonReport.V1Reach(t, shipped, V1ItemLevel).RealmsMilli,
            CraftingHorizonReport.V1Reach(t, flatter, V1ItemLevel).RealmsMilli);
    }
}
