using FusionRpg.Core.Stats.Aptitudes;
using Xunit;

namespace FusionRpg.Core.Tests.ClassSystem;

public class AptitudeAutoAssignTests
{
    [Fact]
    public void FillEven_splits_budget_with_leftover()
    {
        var result = AptitudeAutoAssign.FillEven(100);
        Assert.True(result.Ok);
        // Reads AptitudeCatalog.Count live rather than a second 12 (population-pin SE3.3, 2026-09-19)
        // -- FillEven splits across this same count internally.
        Assert.Equal(AptitudeCatalog.Count, result.Shares.Count);
        Assert.All(result.Shares.Values, v => Assert.Equal(8, v));
        Assert.Equal(100 - AptitudeCatalog.Count * 8, result.Leftover);
    }

    [Fact]
    public void FillPosture_force_puts_all_spend_in_force()
    {
        var result = AptitudeAutoAssign.FillPosture(40, Posture.Force);
        Assert.True(result.Ok);
        var forceIds = AptitudeCatalog.All.Where(a => a.Posture == Posture.Force).Select(a => a.Id).ToHashSet();
        foreach (var (id, share) in result.Shares)
        {
            if (forceIds.Contains(id)) Assert.Equal(10, share);
            else Assert.Equal(0, share);
        }
        Assert.Equal(0, result.Leftover);
    }

    [Fact]
    public void FillFromPermille_empty_favour_refuses_S7()
    {
        var result = AptitudeAutoAssign.FillFromPermille(100, new Dictionary<string, long>());
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.favour.empty", result.Reason);
    }

    /// <summary>W3 — a real plan row (the committed `_species-build-plan.json` shape, e.g.
    /// `abyssswordstar`: Agility 163, Bulwark 163, Composure 162, Ferocity 162, Onslaught 350) carries
    /// at most `maxAptitudesPerSpecies` keys. The other seven aptitudes are zero-filled, never a
    /// refusal (spec-assign-ladder.md W3, testing strategy 1).</summary>
    [Fact]
    public void Fill_species_favour_zero_fills_a_real_five_key_plan_row()
    {
        var fiveKeyPlanRow = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Agility"] = 163,
            ["Bulwark"] = 163,
            ["Composure"] = 162,
            ["Ferocity"] = 162,
            ["Onslaught"] = 350,
        };
        Assert.Equal(1000, fiveKeyPlanRow.Values.Sum());

        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.SpeciesFavour, 1000, favourPermille: fiveKeyPlanRow);

        Assert.True(result.Ok, result.Reason);
        Assert.Equal(AptitudeCatalog.Count, result.Shares.Count);
        foreach (var apt in AptitudeCatalog.All)
            if (!fiveKeyPlanRow.ContainsKey(apt.Id))
                Assert.True(result.Shares.TryGetValue(apt.Id, out var zeroed) && zeroed == 0);
        Assert.Equal(1000, result.Shares.Values.Sum() + result.Leftover);
    }

    [Fact]
    public void Fill_species_favour_unknown_aptitude_id_refuses()
    {
        var favour = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["NotARealAptitude"] = 1000,
        };
        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.SpeciesFavour, 1000, favourPermille: favour);
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.favour.unknownAptitude", result.Reason);
    }

    [Fact]
    public void Fill_species_favour_non_normalised_sum_refuses()
    {
        var favour = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["Might"] = 500,
            ["Agility"] = 400, // sums to 900, not 1000
        };
        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.SpeciesFavour, 1000, favourPermille: favour);
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.favour.notNormalised", result.Reason);
    }

    [Fact]
    public void Fill_species_favour_uses_materialize_math()
    {
        var favour = AptitudeCatalog.All.ToDictionary(a => a.Id, _ => 0L, StringComparer.Ordinal);
        // 1000 across Might only is invalid for ValidateTargetPermilleSum needing twelve rows sum 1000 —
        // give each ~83 with remainder on Might.
        long assigned = 0;
        foreach (var apt in AptitudeCatalog.All)
        {
            favour[apt.Id] = 83;
            assigned += 83;
        }
        favour["Might"] = 83 + (1000 - assigned);

        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.SpeciesFavour, 1000, favourPermille: favour);
        Assert.True(result.Ok, result.Reason);
        Assert.True(result.Leftover >= 0);
        Assert.True(result.Shares.Values.Sum() + result.Leftover == 1000);
    }

    [Fact]
    public void Fill_active_preset_missing_rows_refuses()
    {
        var result = AptitudeAutoAssign.Fill(AptitudeAutoAssignRules.ActivePreset, 100);
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.activePreset.missing", result.Reason);
    }

    [Fact]
    public void Fill_unknown_rule_refuses()
    {
        var result = AptitudeAutoAssign.Fill("nope", 10);
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.rule.unknown", result.Reason);
    }

    [Fact]
    public void Fill_species_favour_refused_when_Mode_C()
    {
        var favour = AptitudeCatalog.All.ToDictionary(a => a.Id, _ => 83L, StringComparer.Ordinal);
        favour["Might"] = 83 + (1000 - 83 * 12);
        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.SpeciesFavour, 1000, favourPermille: favour, favourAllowed: false);
        Assert.False(result.Ok);
        Assert.Equal("autoAssign.favour.modeC", result.Reason);
        Assert.Empty(result.Shares);
    }

    [Fact]
    public void Fill_active_preset_uses_D13_materialize_leftover_legal()
    {
        var rows = AptitudeCatalog.All.Select(a => new AptitudePresetRowSpec(a.Id, 83)).ToList();
        var might = rows.FindIndex(r => r.AptitudeId == "Might");
        rows[might] = rows[might] with { TargetPermille = 83 + (1000 - 83 * 12) };

        var result = AptitudeAutoAssign.Fill(
            AptitudeAutoAssignRules.ActivePreset, 100, activePresetRows: rows);
        Assert.True(result.Ok, result.Reason);
        Assert.True(result.Leftover >= 0);
        Assert.Equal(100, result.Shares.Values.Sum() + result.Leftover);
    }

    [Fact]
    public void FillEven_uses_long_budget_math_S6()
    {
        // Large budget — would overflow int if multiplied as int before cast.
        const long budget = 2_000_000_000L;
        var result = AptitudeAutoAssign.FillEven(budget);
        Assert.True(result.Ok);
        Assert.Equal(budget, result.Shares.Values.Sum() + result.Leftover);
    }

    // EP1.20 (spec-auto-assign-control.md, C1) — `.All` is a closed vocabulary the code owns
    // (six named consts), so pinning its cardinality and membership is the right kind of pin
    // (contrast a derived-population count, which a guardrail never pins).
    [Fact]
    public void Rules_All_names_exactly_the_six_consts_no_duplicates()
    {
        var expected = new[]
        {
            AptitudeAutoAssignRules.ActivePreset, AptitudeAutoAssignRules.SpeciesFavour,
            AptitudeAutoAssignRules.PostureForce, AptitudeAutoAssignRules.PostureFinesse,
            AptitudeAutoAssignRules.PostureBastion, AptitudeAutoAssignRules.Even
        };
        Assert.Equal(6, AptitudeAutoAssignRules.All.Count);
        Assert.Equal(expected.ToHashSet(), AptitudeAutoAssignRules.All.ToHashSet());
        Assert.Equal(AptitudeAutoAssignRules.All.Count, AptitudeAutoAssignRules.All.Distinct().Count());
    }
}
