using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

public class AssignLadderTests
{
    static readonly AssignLadderTuning DefaultOrder = new(new[]
    {
        AptitudeAutoAssignRules.ActivePreset,
        AptitudeAutoAssignRules.SpeciesFavour,
        AssignLadder.PostureRung,
        AptitudeAutoAssignRules.Even,
    });

    static readonly Dictionary<string, long> RealFiveKeyFavour = new(StringComparer.Ordinal)
    {
        ["Agility"] = 163,
        ["Bulwark"] = 163,
        ["Composure"] = 162,
        ["Ferocity"] = 162,
        ["Onslaught"] = 350,
    };

    static AssignContext EmptyContext(bool favourAllowed = true) =>
        new(ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: favourAllowed);

    [Fact]
    public void Totality_every_absent_input_still_returns_even()
    {
        var suggestion = AssignLadder.Suggest(EmptyContext(), DefaultOrder);
        Assert.Equal(AptitudeAutoAssignRules.Even, suggestion.RuleId);
        Assert.Equal(AptitudeCatalog.Count, suggestion.Rows.Count);
        Assert.Equal(1000, suggestion.Rows.Sum(r => r.TargetPermille));
    }

    [Fact]
    public void R23_commander_context_returns_even_with_three_named_skips()
    {
        // No preset, no favour map, no posture, FavourAllowed=false — exactly R23's Zomboss context.
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: false);

        var suggestion = AssignLadder.Suggest(ctx, DefaultOrder);

        Assert.Equal(AptitudeAutoAssignRules.Even, suggestion.RuleId);
        Assert.Equal(3, suggestion.Skipped.Count);
        Assert.Equal(AptitudeAutoAssignRules.ActivePreset, suggestion.Skipped[0].RuleId);
        Assert.Equal(AptitudeAutoAssignRules.SpeciesFavour, suggestion.Skipped[1].RuleId);
        Assert.Equal(AssignLadder.PostureRung, suggestion.Skipped[2].RuleId);
        Assert.All(suggestion.Skipped, s => Assert.False(string.IsNullOrEmpty(s.Reason)));
    }

    [Fact]
    public void Adding_an_active_preset_to_the_R23_context_makes_active_preset_win()
    {
        var rows = FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.All
            .Select(a => new AptitudePresetRowSpec(a.Id, 1000 / 12))
            .ToList();
        var ctx = new AssignContext(
            ActivePresetRows: rows, SpeciesFavourPermille: null, SpeciesPosture: null, FavourAllowed: false);

        var suggestion = AssignLadder.Suggest(ctx, DefaultOrder);

        Assert.Equal(AptitudeAutoAssignRules.ActivePreset, suggestion.RuleId);
        Assert.Empty(suggestion.Skipped);
        Assert.Same(rows, suggestion.Rows);
    }

    [Fact]
    public void Species_favour_wins_from_a_real_five_key_plan_row_zero_filled()
    {
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: RealFiveKeyFavour, SpeciesPosture: null, FavourAllowed: true);

        var suggestion = AssignLadder.Suggest(ctx, DefaultOrder);

        Assert.Equal(AptitudeAutoAssignRules.SpeciesFavour, suggestion.RuleId);
        Assert.Equal(AptitudeCatalog.Count, suggestion.Rows.Count);
        Assert.Equal(1000, suggestion.Rows.Sum(r => r.TargetPermille));
        Assert.Equal(1, suggestion.Skipped.Count); // active-preset only
    }

    [Theory]
    [InlineData(FusionRpg.Core.Stats.Aptitudes.Posture.Force, "posture-force")]
    [InlineData(FusionRpg.Core.Stats.Aptitudes.Posture.Finesse, "posture-finesse")]
    [InlineData(FusionRpg.Core.Stats.Aptitudes.Posture.Bastion, "posture-bastion")]
    public void Posture_resolves_to_one_of_the_three_real_rule_ids_never_a_seventh(
        FusionRpg.Core.Stats.Aptitudes.Posture posture, string expectedRuleId)
    {
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: null, SpeciesPosture: posture, FavourAllowed: true);

        var suggestion = AssignLadder.Suggest(ctx, DefaultOrder);

        Assert.Equal(expectedRuleId, suggestion.RuleId);
        Assert.Equal(AptitudeCatalog.Count, suggestion.Rows.Count);
        Assert.Equal(1000, suggestion.Rows.Sum(r => r.TargetPermille));
        var inPosture = FusionRpg.Core.Stats.Aptitudes.AptitudeCatalog.All.Where(a => a.Posture == posture).Select(a => a.Id).ToHashSet();
        foreach (var row in suggestion.Rows)
            Assert.Equal(inPosture.Contains(row.AptitudeId) ? 250 : 0, row.TargetPermille);
    }

    [Fact]
    public void Ladder_order_is_data_two_orders_pick_two_different_winners()
    {
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: RealFiveKeyFavour,
            SpeciesPosture: FusionRpg.Core.Stats.Aptitudes.Posture.Bastion, FavourAllowed: true);

        var favourFirst = new AssignLadderTuning(new[]
        {
            AptitudeAutoAssignRules.SpeciesFavour, AssignLadder.PostureRung, AptitudeAutoAssignRules.Even,
        });
        var postureFirst = new AssignLadderTuning(new[]
        {
            AssignLadder.PostureRung, AptitudeAutoAssignRules.SpeciesFavour, AptitudeAutoAssignRules.Even,
        });

        var a = AssignLadder.Suggest(ctx, favourFirst);
        var b = AssignLadder.Suggest(ctx, postureFirst);

        Assert.Equal(AptitudeAutoAssignRules.SpeciesFavour, a.RuleId);
        Assert.Equal("posture-bastion", b.RuleId);
        Assert.NotEqual(a.RuleId, b.RuleId);
    }

    [Fact]
    public void Empty_favour_map_skips_with_named_reason_and_walk_continues()
    {
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: new Dictionary<string, long>(),
            SpeciesPosture: null, FavourAllowed: true);

        var suggestion = AssignLadder.Suggest(ctx, DefaultOrder);

        Assert.Equal(AptitudeAutoAssignRules.Even, suggestion.RuleId);
        var favourSkip = suggestion.Skipped.Single(s => s.RuleId == AptitudeAutoAssignRules.SpeciesFavour);
        Assert.Equal("autoAssign.favour.empty", favourSkip.Reason);
    }

    [Fact]
    public void An_unknown_rung_in_the_order_refuses_and_is_skipped_with_a_named_reason()
    {
        var tuning = new AssignLadderTuning(new[] { "nope", AptitudeAutoAssignRules.Even });
        var suggestion = AssignLadder.Suggest(EmptyContext(), tuning);
        Assert.Equal(AptitudeAutoAssignRules.Even, suggestion.RuleId);
        Assert.Equal("assignLadder.rung.unknown", suggestion.Skipped.Single().Reason);
    }

    // ---- TryOne: the explicit single-rule path (EP1.4's "rule given") ----------------------------

    [Fact]
    public void TryOne_even_never_fails_and_never_reads_the_context()
    {
        var (ok, rows, reason) = AssignLadder.TryOne(AptitudeAutoAssignRules.Even, EmptyContext(favourAllowed: false));
        Assert.True(ok, reason);
        Assert.Equal(1000, rows.Sum(r => r.TargetPermille));
    }

    [Theory]
    [InlineData("posture-force")]
    [InlineData("posture-finesse")]
    [InlineData("posture-bastion")]
    public void TryOne_accepts_a_raw_posture_id_directly_without_a_known_context_posture(string ruleId)
    {
        // No SpeciesPosture on the context at all -- a direct posture-* call names the posture
        // itself, unlike the "posture" resolver token, which needs SpeciesPosture set.
        var (ok, rows, reason) = AssignLadder.TryOne(ruleId, EmptyContext());
        Assert.True(ok, reason);
        Assert.Equal(1000, rows.Sum(r => r.TargetPermille));
    }

    [Fact]
    public void TryOne_the_posture_resolver_token_still_needs_a_known_posture()
    {
        var (ok, _, reason) = AssignLadder.TryOne(AssignLadder.PostureRung, EmptyContext());
        Assert.False(ok);
        Assert.Equal("autoAssign.posture.unknown", reason);
    }

    [Fact]
    public void TryOne_species_favour_uses_the_same_zero_fill_as_the_walk()
    {
        var ctx = new AssignContext(
            ActivePresetRows: null, SpeciesFavourPermille: RealFiveKeyFavour, SpeciesPosture: null, FavourAllowed: true);
        var (ok, rows, reason) = AssignLadder.TryOne(AptitudeAutoAssignRules.SpeciesFavour, ctx);
        Assert.True(ok, reason);
        Assert.Equal(1000, rows.Sum(r => r.TargetPermille));
    }

    [Fact]
    public void TryOne_an_unknown_rule_refuses()
    {
        var (ok, _, reason) = AssignLadder.TryOne("nope", EmptyContext());
        Assert.False(ok);
        Assert.Equal("assignLadder.rung.unknown", reason);
    }
}
