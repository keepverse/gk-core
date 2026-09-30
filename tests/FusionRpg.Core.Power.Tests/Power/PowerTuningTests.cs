using System;
using System.IO;
using FusionRpg.Core.Power;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Power;

/// <summary>
/// power-ladder wave 1 (spec-power-ladder.md §2.4, §5). Covers <see cref="PowerTuning.Build"/> and
/// <see cref="PowerTuningLoader.Parse"/> — every load-time rejection, the pin-preserving derivation
/// of A across the legal B range, and that the shipped tuning file itself parses. PowerLadder.Value's
/// own behaviour (the function, maxIndex, determinism) is PowerLadderTests.cs, not here.
/// </summary>
public class PowerTuningTests
{
    // ---- fixtures ------------------------------------------------------------------------------

    const long FixedC = PowerTuning.FixedCMilli;
    const int FixedPinIndex = PowerTuning.FixedPinIndex;
    const long FixedPinValue = PowerTuning.FixedPinValue;

    static PowerTuning Build(long bMilli = 0,
        long wd = 1000, long wa = 25000, long wr = 250, long wz = 1000, long? wm = 5000, long ww = 5000, long wf = 25000,
        long cMilli = FixedC, int pinIndex = FixedPinIndex, long pinValue = FixedPinValue) =>
        PowerTuning.Build(schemaVersion: 1, version: 1, cMilli, bMilli, pinIndex, pinValue, wd, wa, wr, wz, wm, ww, wf);

    static string Json(long bMilli, string wmField)
    {
        return "{ \"schemaVersion\": 1, \"version\": 1, "
            + "\"curve\": { \"cMilli\": 80000, \"bMilli\": " + bMilli + ", \"pinIndex\": 20, \"pinValue\": 680 }, "
            + "\"weights\": { \"WdMilli\": 1000, \"WaMilli\": 25000, \"WrMilli\": 250, \"WzMilli\": 1000, "
            + "\"WmMilli\": " + wmField + ", \"WwMilli\": 5000, \"WfMilli\": 25000 } }";
    }

    // ---- A derivation ----------------------------------------------------------------------------

    [Fact]
    public void Build_AtBZero_DerivesAMilli30000()
    {
        var t = Build(bMilli: 0);
        Assert.Equal(30000, t.Curve.AMilli);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(200)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(9998)]
    public void Build_ADerivation_MatchesClosedFormForEveryLegalB(long bMilli)
    {
        // A_milli = 30000 - 19*B_milli/2 (§2.2), specialised from the general pin-solve for the
        // fixed C=80000/pinIndex=20/pinValue=680 — exact because bMilli is even.
        var t = Build(bMilli: bMilli);
        Assert.Equal(30000 - 19 * bMilli / 2, t.Curve.AMilli);
    }

    [Fact]
    public void Build_PinHolds_ReconstructedMilliEqualsPinValueTimes1000()
    {
        // The belt-and-braces check (PinBroken) never throws for legal input by construction — this
        // proves the invariant it guards holds across the whole legal B range, since Build() would
        // have thrown PinBroken otherwise.
        foreach (var b in new long[] { 0, 2, 200, 400, 1000, 9998 })
        {
            var t = Build(bMilli: b);
            long reconstructed = t.Curve.CMilli + t.Curve.AMilli * t.Curve.PinIndex
                + t.Curve.BMilli * t.Curve.PinIndex * (t.Curve.PinIndex - 1) / 2;
            Assert.Equal(t.Curve.PinValue * 1000L, reconstructed);
        }
    }

    // ---- rejections ------------------------------------------------------------------------------

    [Fact]
    public void Build_OddB_RejectsNamingNearestLegalValues()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => Build(bMilli: 401));
        Assert.Equal(PowerRejectionReason.OddB, ex.Reason);
        Assert.Contains("400", ex.Message);
        Assert.Contains("402", ex.Message);
    }

    [Fact]
    public void Build_NegativeB_Rejects()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => Build(bMilli: -2));
        Assert.Equal(PowerRejectionReason.NegativeB, ex.Reason);
    }

    [Fact]
    public void Build_AbsurdBMilli_ThrowsOverflowRatherThanWrapping()
    {
        // bMilli is operator-authored config, not a bounded runtime value. docs/architecture/numeric-types.md: "overflow
        // throws, never wraps; no silent unchecked on a magnitude path" — a pathological value must
        // never silently wrap into a plausible-looking (wrong) curve.
        Assert.Throws<OverflowException>(() => Build(bMilli: long.MaxValue / 2));
    }

    [Theory]
    [InlineData("cMilli")]
    [InlineData("pinIndex")]
    [InlineData("pinValue")]
    public void Build_ChangedFixedConstant_Rejects(string which)
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => which switch
        {
            "cMilli" => Build(cMilli: FixedC + 1),
            "pinIndex" => Build(pinIndex: FixedPinIndex + 1),
            _ => Build(pinValue: FixedPinValue + 1),
        });
        Assert.Equal(PowerRejectionReason.FixedConstantChanged, ex.Reason);
    }

    [Theory]
    [InlineData("Wd")]
    [InlineData("Wa")]
    [InlineData("Wr")]
    [InlineData("Wz")]
    [InlineData("Ww")]
    [InlineData("Wf")]
    [InlineData("Wm")]
    public void Build_NegativeWeight_RejectsPerComponent(string which)
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => which switch
        {
            "Wd" => Build(wd: -1),
            "Wa" => Build(wa: -1),
            "Wr" => Build(wr: -1),
            "Wz" => Build(wz: -1),
            "Ww" => Build(ww: -1),
            "Wf" => Build(wf: -1),
            _ => Build(wm: -1),
        });
        Assert.Equal(PowerRejectionReason.NegativeWeight, ex.Reason);
        Assert.Contains(which, ex.Message);
    }

    [Fact]
    public void Build_WmMilliNull_IsLegalAtRest()
    {
        var t = Build(wm: null);
        Assert.Null(t.Weights.WmMilli);
    }

    // ---- Parse: JSON-level rejections --------------------------------------------------------------

    [Fact]
    public void Parse_EmptyDocument_RejectsTuningMissing()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => PowerTuningLoader.Parse(""));
        Assert.Equal(PowerRejectionReason.TuningMissing, ex.Reason);
    }

    [Fact]
    public void Parse_InvalidJson_RejectsTuningMissing()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => PowerTuningLoader.Parse("{ not json"));
        Assert.Equal(PowerRejectionReason.TuningMissing, ex.Reason);
    }

    [Fact]
    public void Parse_MissingCurveObject_RejectsTuningMissing()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => PowerTuningLoader.Parse(
            "{ \"schemaVersion\": 1, \"version\": 1, \"weights\": {} }"));
        Assert.Equal(PowerRejectionReason.TuningMissing, ex.Reason);
    }

    [Fact]
    public void Parse_MissingWeightsObject_RejectsTuningMissing()
    {
        var ex = Assert.Throws<PowerTuningRejection>(() => PowerTuningLoader.Parse(
            "{ \"schemaVersion\": 1, \"version\": 1, \"curve\": { \"cMilli\": 80000, \"bMilli\": 0, \"pinIndex\": 20, \"pinValue\": 680 } }"));
        Assert.Equal(PowerRejectionReason.TuningMissing, ex.Reason);
    }

    [Fact]
    public void Parse_MissingWmField_RejectsTuningMissing()
    {
        // Wm may be JSON null (legal at rest), but the key itself must be present — an absent key
        // is a malformed document, not "no weight yet".
        var json = "{ \"schemaVersion\": 1, \"version\": 1, "
            + "\"curve\": { \"cMilli\": 80000, \"bMilli\": 0, \"pinIndex\": 20, \"pinValue\": 680 }, "
            + "\"weights\": { \"WdMilli\": 1000, \"WaMilli\": 25000, \"WrMilli\": 250, \"WzMilli\": 1000, \"WwMilli\": 5000, \"WfMilli\": 25000 } }";
        var ex = Assert.Throws<PowerTuningRejection>(() => PowerTuningLoader.Parse(json));
        Assert.Equal(PowerRejectionReason.TuningMissing, ex.Reason);
    }

    // ---- Parse: success paths ----------------------------------------------------------------------

    [Fact]
    public void Parse_WellFormedDocument_Succeeds()
    {
        var t = PowerTuningLoader.Parse(Json(bMilli: 400, wmField: "5000"));
        Assert.Equal(1, t.SchemaVersion);
        Assert.Equal(400, t.Curve.BMilli);
        Assert.Equal(26200, t.Curve.AMilli);
        Assert.Equal(5000, t.Weights.WmMilli);
    }

    [Fact]
    public void Parse_WmMilliNullInJson_IsLegalAtRest()
    {
        var t = PowerTuningLoader.Parse(Json(bMilli: 0, wmField: "null"));
        Assert.Null(t.Weights.WmMilli);
    }

    [Fact]
    public void Parse_ShippedPowerScaleV1_ParsesAndShipsBZeroForZeroGoldenMovement()
    {
        // Phase 1-3 ship inert/adopting at B=0 (plan.md architecture decision 1); power-dial (T4.2)
        // is the one commit that republishes v2 at B=400. If this ever reads non-zero, Phase 2's
        // "zero golden movement" premise is silently broken before a single consumer is wired.
        var path = Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v1.json");
        var t = PowerTuningLoader.Parse(File.ReadAllText(path));

        Assert.Equal(0, t.Curve.BMilli);
        Assert.Equal(30000, t.Curve.AMilli);
        Assert.Equal(680, t.Curve.PinValue);
        Assert.Equal(5000, t.Weights.WmMilli); // decided (power-map.md), not the spec-example null
        Assert.Equal(t.Weights.WaMilli, t.Weights.WfMilli); // Wf = Wa invariant, SSOT §5.1
    }

    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    // ---- power-scale.v3 (passive-tree-repair V3-1): pins, not curves -------------------------------

    static PowerTuning LoadV3() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v3.json")));

    static PowerTuning LoadV2() =>
        PowerTuningLoader.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "tuning", "power-scale.v2.json")));

    [Fact]
    public void Parse_V3Channels_LoadWithReasonKeysIgnored()
    {
        // T3: every pin carries its reason inline. The loader reads cMilli/pinValue and ignores
        // the prose — a reason must never become load-bearing or load-breaking.
        var t = LoadV3();
        Assert.NotEmpty(t.ChannelsOrEmpty);
        foreach (var (name, ch) in t.ChannelsOrEmpty)
        {
            Assert.True(ch.PinValue > 0, $"v3 channel '{name}' pinValue must be positive");
            Assert.True(ch.CMilli >= 0, $"v3 channel '{name}' cMilli must not be negative");
        }
    }

    [Fact]
    public void V3Channels_DisjointFromV2()
    {
        // Pins are additive across versions, never overlapping — a collision is a publish error
        // (FamilyExpandGen refuses it at startup for the same reason).
        var v2 = LoadV2();
        var v3 = LoadV3();
        foreach (var name in v3.ChannelsOrEmpty.Keys)
            Assert.DoesNotContain(name, (System.Collections.Generic.IEnumerable<string>)v2.ChannelsOrEmpty.Keys);
    }

    [Fact]
    public void V3Channels_EveryDerivedAStaysPositiveAtDecidedDial()
    {
        // F1 regression extended to the v3 set: a shared absolute B must not drive any channel's
        // derived A negative (audit F1). Data-driven from the file — no pin is quoted here.
        var v3 = LoadV3();
        foreach (var (name, ch) in v3.ChannelsOrEmpty)
        {
            var ladder = new ChannelLadder(v3.Curve.BMilli, v3.Curve.PinValue, ch);
            Assert.True(ladder.AMilliNumerator > 0, $"v3 channel '{name}' derived A must be > 0 (F1)");
        }
    }

    [Fact]
    public void V3RatePins_MatchCodeBasesAtReference()
    {
        // Rate pins are READ from BattleModels' Theta-linear bases at Theta=20, not authored.
        // Literals below are the code formulas evaluated, pinned with their derivation so a
        // formula change fails loudly here instead of silently repricing the corpus.
        var v3 = LoadV3();
        Assert.Equal(FusionRpg.Core.Battle.BattleRuleset.BaseAccuracy(20), v3.ChannelsOrEmpty["combat.accuracy.{variant}"].PinValue); // 220+26*20
        Assert.Equal(FusionRpg.Core.Battle.BattleRuleset.BaseDodge(20), v3.ChannelsOrEmpty["combat.dodge.{variant}"].PinValue); // 26*20
        Assert.Equal(FusionRpg.Core.Battle.BattleRuleset.BaseCritRate(20), v3.ChannelsOrEmpty["combat.crit.rate.{variant}"].PinValue); // 10*20
        Assert.Equal(FusionRpg.Core.Battle.BattleRuleset.BaseCritResist(20), v3.ChannelsOrEmpty["combat.crit.resist.{variant}"].PinValue); // 10*20+250
    }
}
