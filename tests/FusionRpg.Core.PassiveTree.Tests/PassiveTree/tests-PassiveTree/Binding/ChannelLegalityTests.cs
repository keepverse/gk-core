using System.IO;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.PassiveTree.Binding;
using FusionRpg.Core.PassiveTree.Catalog;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.PassiveTree.Binding;

/// <summary>Task D1 — `ChannelLegality` (spec-tree-binder.md §3.3, §4.1, §4.2, §6 M3, §6 M3a). Every
/// expected refusal here is read off the spec's own rule table, not re-derived.</summary>
public class ChannelLegalityTests
{
    static NodeAtom Atom(string channelId, NodeAtomOp op, ScaleAxis axis, UnitClass unitClass, long kMicro = 100) =>
        new("stat.derived", AttachPoint.Stat, channelId, op, null, null, kMicro, axis, unitClass);

    // ---- 1. Thirteen classes, enumerated, never counted -----------------------------------

    [Fact]
    public void All_thirteen_UnitClass_values_carry_a_verdict_and_the_four_buckets_sum_to_13()
    {
        var values = Enum.GetValues<UnitClass>();
        // pin: closed-vocabulary UnitClass — a new unit class is a reviewed change
        Assert.Equal(13, values.Length); // the corpus itself — not the assertion below

        var counts = new Dictionary<ChannelLegality.Verdict, int>();
        foreach (var uc in values)
        {
            // Every single value must resolve without throwing — a 14th class with no verdict
            // throws from VerdictFor's own default arm, failing this loop rather than falling
            // through silently.
            var verdict = ChannelLegality.VerdictFor(uc);
            counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
        }

        Assert.Equal(3, counts[ChannelLegality.Verdict.LadderScaled]);
        Assert.Equal(1, counts[ChannelLegality.Verdict.FlatPermilleOnly]);
        Assert.Equal(3, counts[ChannelLegality.Verdict.ThetaLinear]);
        Assert.Equal(6, counts[ChannelLegality.Verdict.Refused]);
        Assert.Equal(13, counts.Values.Sum()); // 3 + 1 + 3 + 6 = 13, by enumeration
    }

    [Theory]
    [InlineData(UnitClass.GameUnits)]
    [InlineData(UnitClass.GameUnitsPerSecond)]
    [InlineData(UnitClass.ReciprocalPoints)]
    public void The_three_ladder_scaled_classes_bind_PTheta(UnitClass unitClass)
    {
        Assert.Equal(ChannelLegality.Verdict.LadderScaled, ChannelLegality.VerdictFor(unitClass));
        Assert.Equal(ScaleAxis.PTheta, ChannelLegality.ExpectedAxis(unitClass));
        ChannelLegality.CheckBind(Atom("combat.power.fire", NodeAtomOp.Flat, ScaleAxis.PTheta, unitClass));
    }

    [Theory]
    [InlineData(UnitClass.SigmoidPoints)]
    [InlineData(UnitClass.SigmoidMultiplierPoints)]
    [InlineData(UnitClass.StatusPotencyPoints)]
    public void The_three_theta_linear_classes_bind_Theta_and_refuse_a_PTheta_amount_with_the_class_named(UnitClass unitClass)
    {
        Assert.Equal(ChannelLegality.Verdict.ThetaLinear, ChannelLegality.VerdictFor(unitClass));
        Assert.Equal(ScaleAxis.Theta, ChannelLegality.ExpectedAxis(unitClass));

        // Legal: Θ-linear.
        ChannelLegality.CheckBind(Atom("combat.accuracy.fire", NodeAtomOp.Flat, ScaleAxis.Theta, unitClass));

        // Refused: a P(Θ) (PTheta-axis) amount on a contest class.
        var ex = Assert.Throws<BindRefusal>(() =>
            ChannelLegality.CheckBind(Atom("combat.accuracy.fire", NodeAtomOp.Flat, ScaleAxis.PTheta, unitClass)));
        Assert.Contains(unitClass.ToString(), ex.Message);
        Assert.Contains("Θ-linear", ex.Message);
    }

    // ---- 2. PerMilleRatio — flat only, never ladder- or Θ-scaled --------------------------

    [Fact]
    public void PerMilleRatio_binds_flat_only()
    {
        Assert.Equal(ChannelLegality.Verdict.FlatPermilleOnly, ChannelLegality.VerdictFor(UnitClass.PerMilleRatio));
        Assert.Equal(ScaleAxis.FlatPermille, ChannelLegality.ExpectedAxis(UnitClass.PerMilleRatio));
        ChannelLegality.CheckBind(Atom("combat.reflect.rate.omni", NodeAtomOp.Flat, ScaleAxis.FlatPermille, UnitClass.PerMilleRatio));
    }

    [Fact]
    public void PerMilleRatio_claiming_PTheta_is_refused()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("combat.reflect.rate.omni", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.PerMilleRatio)));
        Assert.Contains("PerMilleRatio", ex.Message);
        Assert.Contains("flat per-mille only", ex.Message);
    }

    [Fact]
    public void PerMilleRatio_claiming_Theta_is_refused()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("combat.reflect.rate.omni", NodeAtomOp.Flat, ScaleAxis.Theta, UnitClass.PerMilleRatio)));
        Assert.Contains("PerMilleRatio", ex.Message);
        Assert.Contains("flat per-mille only", ex.Message);
    }

    // ---- 3. The six outright-refuse classes, each named ------------------------------------

    [Fact]
    public void Milliseconds_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("icd_ms", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.Milliseconds)));
        Assert.Contains("Milliseconds", ex.Message);
        Assert.Contains("refused", ex.Message);
    }

    [Fact]
    public void Count_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("maxTargets", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.Count)));
        Assert.Contains("Count", ex.Message);
    }

    [Fact]
    public void Flag_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("status.immune.fire", NodeAtomOp.Flag, ScaleAxis.PTheta, UnitClass.Flag)));
        Assert.Contains("Flag", ex.Message);
    }

    [Fact]
    public void LadderIndex_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("progression.power", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.LadderIndex)));
        Assert.Contains("LadderIndex", ex.Message);
    }

    [Fact]
    public void AptitudePoints_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("might", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.AptitudePoints)));
        Assert.Contains("AptitudePoints", ex.Message);
    }

    [Fact]
    public void LoamUnits_is_refused_as_a_magnitude_target()
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("loam", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.LoamUnits)));
        Assert.Contains("LoamUnits", ex.Message);
    }

    [Fact]
    public void A_refused_class_is_refused_regardless_of_scaleAxis()
    {
        // FlatPermille is Milliseconds' "wrong" axis either way -- the point is it never matters,
        // because the outright-refuse check fires before any axis is even consulted.
        Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("icd_ms", NodeAtomOp.Flat, ScaleAxis.FlatPermille, UnitClass.Milliseconds)));
        Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom("icd_ms", NodeAtomOp.Flat, ScaleAxis.Theta, UnitClass.Milliseconds)));
    }

    // ---- 4. combat.parry.break.* / combat.block.break.* are switches, not dials -----------

    [Theory]
    [InlineData(DerivedStatChannels.CombatParryBreakOmni)]
    [InlineData(DerivedStatChannels.CombatBlockBreakOmni)]
    public void Parry_and_block_break_channels_bind_flat_and_refuse_a_powerLadder_amount(string channelId)
    {
        // Granted flat per-mille — legal (M3a: these two layers reach zero by plain subtraction,
        // so they are budgeted as switches, never ladder-scaled).
        ChannelLegality.CheckBind(Atom(channelId, NodeAtomOp.Flat, ScaleAxis.FlatPermille, UnitClass.PerMilleRatio));

        // A powerLadder-scaled (PTheta-axis) amount is refused -- these channels are PerMilleRatio,
        // so this is the general PerMilleRatio rule, proven here against the exact channel ids M3
        // uses (spec-tree-binder.md §6 M3, §6 M3a).
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom(channelId, NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.PerMilleRatio)));
        Assert.Contains(channelId, ex.Message);
        Assert.Contains("flat per-mille only", ex.Message);
    }

    [Fact]
    public void Parry_and_block_break_channel_ids_match_the_spec_worked_example()
    {
        // §6 M3's worked example authors exactly these two channels at "combat.parry.break.omni" /
        // "combat.block.break.omni" -- confirming the registry constants against the spec text.
        Assert.Equal("combat.parry.break.omni", DerivedStatChannels.CombatParryBreakOmni);
        Assert.Equal("combat.block.break.omni", DerivedStatChannels.CombatBlockBreakOmni);
        Assert.StartsWith(DerivedStatChannels.CombatParryBreakPrefix, DerivedStatChannels.CombatParryBreakOmni);
        Assert.StartsWith(DerivedStatChannels.CombatBlockBreakPrefix, DerivedStatChannels.CombatBlockBreakOmni);
    }

    // ---- 5. The five LowerIsBetter primaries refuse a "+X" ---------------------------------

    [Theory]
    [InlineData("attackInterval")]
    [InlineData("produceInterval")]
    [InlineData("attackCountdown")]
    [InlineData("produceCountdown")]
    [InlineData("takeDmgMultiplier")]
    public void Every_LowerIsBetter_primary_refuses_a_Flat_plus_X(string channel)
    {
        Assert.Contains(channel, ChannelLegality.LowerIsBetterPrimaries);
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom(channel, NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.GameUnits)));
        Assert.Contains(channel, ex.Message);
        Assert.Contains("LowerIsBetter", ex.Message);
    }

    [Theory]
    [InlineData("attackInterval")]
    [InlineData("produceInterval")]
    [InlineData("attackCountdown")]
    [InlineData("produceCountdown")]
    [InlineData("takeDmgMultiplier")]
    public void Every_LowerIsBetter_primary_refuses_an_Increased_plus_X(string channel)
    {
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            Atom(channel, NodeAtomOp.Increased, ScaleAxis.FlatPermille, UnitClass.PerMilleRatio)));
        Assert.Contains(channel, ex.Message);
        Assert.Contains("LowerIsBetter", ex.Message);
    }

    [Fact]
    public void LowerIsBetterPrimaries_is_exactly_five_and_matches_ModifierOp_DirectionOf()
    {
        Assert.Equal(5, ChannelLegality.LowerIsBetterPrimaries.Count);
        foreach (var channel in ChannelLegality.LowerIsBetterPrimaries)
            Assert.True(FusionRpg.Core.Stats.StatChannels.IsLowerBetter(channel));
    }

    [Fact]
    public void A_HigherIsBetter_primary_channel_accepts_a_Flat_plus_X()
    {
        // Control: the direction check must not fire on an ordinary HigherIsBetter primary.
        ChannelLegality.CheckBind(Atom("atk", NodeAtomOp.Flat, ScaleAxis.PTheta, UnitClass.GameUnits));
    }

    // ---- 6. M3 — there is no `More` on the derived side ------------------------------------

    /// <summary>Superseded 2026-09-13 (task P4.2, R2). This test used to assert `NodeAtomOp` had **no**
    /// `More` member, so a more-op atom was "structurally unrepresentable". That worked only because
    /// the tree vocabulary could never name `More` — but `stat.modify` legitimately supports it
    /// (`AtomKindRegistry.cs:517`, `AtomRowValidator.StatOps`), and the SAME enum serves both kinds.
    /// The absence therefore refused 80 real nodes' `more` ops (measured 2026-09-13) at
    /// `TreeBinderRun.ParseOp`. P4.2 adds the member and moves M3 from a structural property to a
    /// NAMED, KIND-AWARE refusal — which this test now proves is still enforced, at load and at bind,
    /// so the loud refusal cannot decay into the silent drop `TreeAtomSource` would otherwise perform.
    /// </summary>
    [Fact]
    public void More_exists_in_the_vocabulary_and_is_refused_on_the_derived_side_by_name()
    {
        // The member exists now ...
        Assert.Contains(Enum.GetNames<NodeAtomOp>(), name => name.Equals("More", StringComparison.OrdinalIgnoreCase));
        Assert.True(Enum.TryParse<NodeAtomOp>("more", ignoreCase: true, out var more));
        Assert.Equal(NodeAtomOp.More, more);

        // ... and M3 is still loud: a stat.derived atom bearing it is a named BindRefusal, never a
        // silent skip. (The catalog loader's OWN M3 arm is proven separately in
        // PassiveTreeCatalogLoaderTests.Derived_atom_with_a_more_op_is_refused_by_name_at_load; this
        // proves the BIND-time arm, which is what a tree run actually goes through.)
        var derivedMore = new NodeAtom("stat.derived", AttachPoint.Stat, "combat.power.fire",
            NodeAtomOp.More, null, null, 100, ScaleAxis.PTheta, UnitClass.GameUnits);
        var ex = Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(derivedMore));
        Assert.Contains("more", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derived", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("M3", ex.Message);

        Assert.Equal("M3", ChannelLegality.NoMoreOnDerivedRuleName);
    }

    /// <summary>P4.2's core hazard, proven directly: the whole point of the explicit M3 check is that a
    /// derived `More` must NOT reach the resolve path, where `TreeAtomSource.BoundAtomsFor` would skip
    /// it silently (`AtomDerivedSubsystem.TryParseOp` has no "more" arm). This proves the refusal fires
    /// for EVERY kind spelling that could carry it, so no path lets it through to a silent drop.</summary>
    [Fact]
    public void A_derived_More_is_refused_but_a_primary_More_is_not_the_exact_P4_2_distinction()
    {
        Assert.Throws<BindRefusal>(() => ChannelLegality.CheckBind(
            new NodeAtom("stat.derived", AttachPoint.Stat, "combat.power.fire",
                NodeAtomOp.More, null, null, 100, ScaleAxis.PTheta, UnitClass.GameUnits)));

        // The same op on the kind that owns it is accepted -- both halves matter: refusing both would
        // not fix the 80-node defect, and accepting both would create the silent no-op.
        ChannelLegality.CheckBind(
            new NodeAtom("stat.modify", AttachPoint.Stat, "atk",
                NodeAtomOp.More, null, null, 100, ScaleAxis.PTheta, UnitClass.GameUnits));
    }

    [Fact]
    public void More_is_legal_on_a_primary_stat_modify_channel()
    {
        // The other half: `more` is a real op for stat.modify (FA1's own vocabulary), so a
        // stat.modify atom must NOT be refused for it. This is the exact case the 80 measured
        // refusals were.
        var primaryMore = new NodeAtom("stat.modify", AttachPoint.Stat, "atk",
            NodeAtomOp.More, null, null, 100, ScaleAxis.PTheta, UnitClass.GameUnits);
        ChannelLegality.CheckBind(primaryMore); // must not throw
    }

    // ---- 7. channelAnchorMilli follows a moved pin, with no source edit --------------------

    [Fact]
    public void ChannelAnchorMilli_follows_atk_pinValue_when_the_tuning_file_moves_it()
    {
        var baseline = PowerTuningLoader.Parse(SyntheticPowerTuningJson(atkPinValue: 92));
        Assert.Equal(135L, ChannelAnchor.ForChannel("atk", baseline));

        // Move the pin -- nothing in ChannelAnchor.cs is touched, only the tuning input.
        // round_half_away(184*1000, 680): q=270, r=400, 2r=800 >= 680 -> q+1 = 271.
        var moved = PowerTuningLoader.Parse(SyntheticPowerTuningJson(atkPinValue: 184));
        Assert.Equal(271L, ChannelAnchor.ForChannel("atk", moved));
        Assert.NotEqual(ChannelAnchor.ForChannel("atk", baseline), ChannelAnchor.ForChannel("atk", moved));
    }

    static string SyntheticPowerTuningJson(long atkPinValue) => $$"""
        {
          "schemaVersion": 1,
          "version": 999,
          "curve": { "cMilli": 80000, "bMilli": 400, "pinIndex": 20, "pinValue": 680 },
          "weights": {
            "WdMilli": 1000, "WaMilli": 25000, "WrMilli": 250, "WzMilli": 1000,
            "WmMilli": 5000, "WwMilli": 5000, "WfMilli": 25000
          },
          "channels": {
            "atk":     { "cMilli": 12000, "pinValue": {{atkPinValue}} },
            "defense": { "cMilli": 2000,  "pinValue": 22 }
          }
        }
        """;
}
