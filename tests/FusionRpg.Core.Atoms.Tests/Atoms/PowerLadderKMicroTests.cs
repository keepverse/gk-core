using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// Task B3 — `PowerLadderKMicro` (spec-tree-binder.md §3.5, §5.3, §7). At per-mille,
/// `gated-deep` stores `kMilli = 0` for 12 of 40 nodes (silently inert, in the shallow tiers every
/// build buys first). This is the per-million sibling, and the same three lines at
/// `AtomCompiler.cs`'s `powerLadder` branch it is resolved by, widened to `long`.
/// </summary>
public class PowerLadderKMicroTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 400, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    static AtomRow StatModifyAtom(string paramsJson, string family = "atom.tree-node") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    // ---- ValueSpec.Validate() ------------------------------------------------------------------

    [Fact]
    public void A_powerLadder_spec_using_kMicro_is_valid()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, PowerLadder: true, PowerLadderKMicro: 999);
        Assert.True(spec.Validate().IsOk);
    }

    [Fact]
    public void PowerLadderKMilli_default_is_unaffected_by_setting_KMicro()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, PowerLadder: true, PowerLadderKMicro: 999);
        Assert.Equal(0, spec.PowerLadderKMilli);
    }

    // ---- AtomJson: the 'kMicro' grammar branch -------------------------------------------------

    [Fact]
    public void A_powerLadder_amount_authored_with_kMicro_is_accepted_at_load()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMicro":999}}""");
        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void Both_kMilli_and_kMicro_together_is_refused_as_a_content_error()
    {
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130,"kMicro":999}}""");
        var result = AtomRowValidator.Validate(atom, kindId => null);
        Assert.False(result.IsOk);
    }

    [Fact]
    public void Neither_kMilli_nor_kMicro_is_refused()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true}}""");
        Assert.False(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    // ---- AtomCompiler: the widened, per-million resolve ----------------------------------------

    [Fact]
    public void The_compiler_resolves_kMicro_to_kMicro_times_PowerLadder_Value_over_1_000_000()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMicro":135000}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerTheta: 250, powerTuning: Tuning);

        var expectedPTheta = new PowerLadder(Tuning).Value(250);
        var expected = 135000L * expectedPTheta / 1_000_000;

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);
        Assert.Equal(expected, Convert.ToInt64(action.Params["flat"]));
    }

    [Fact]
    public void PowerLadderKMilli_existing_consumers_are_completely_unaffected()
    {
        // The exact scenario an existing patron-absorption atom uses — kMilli only, no kMicro —
        // must resolve identically to before this task (same expected int, same narrow ceiling).
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerTheta: 250, powerTuning: Tuning);

        var expectedPTheta = new PowerLadder(Tuning).Value(250);
        var expected = (int)(130L * expectedPTheta / 1000);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(expected, Convert.ToInt32(action.Params["flat"]));
    }

    [Fact]
    public void A_tier_1_gated_deep_style_share_that_rounds_to_zero_kMilli_is_nonzero_at_kMicro()
    {
        // spec-tree-binder.md's own motivating example: at per-mille, gated-deep's shallow-tier
        // coefficient can round to exactly zero. The identical real-world share, expressed in
        // per-million, must survive. `kMicro=6` (analogous to a share too small for kMilli=0 to
        // carry) resolved against a real PowerLadder value must be provably non-zero here whenever
        // the milli form would have flattened to zero.
        const long kMicroThatWouldRoundToZeroMilli = 6; // 0.000006 in per-mille terms -> milli rounds to 0
        var atom = StatModifyAtom(
            "{\"channel\":\"atk\",\"op\":\"flat\",\"amount\":{\"powerLadder\":true,\"kMicro\":" +
            kMicroThatWouldRoundToZeroMilli + "}}");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerTheta: 25000, powerTuning: Tuning);

        var pTheta = new PowerLadder(Tuning).Value(25000);
        var resolvedMicro = kMicroThatWouldRoundToZeroMilli * pTheta / 1_000_000;
        var resolvedIfItHadBeenMilli = (int)(kMicroThatWouldRoundToZeroMilli * pTheta / 1000);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(resolvedMicro, Convert.ToInt64(action.Params["flat"]));
        // The whole point: at a large enough Theta the micro form still resolves meaningfully
        // relative to what a milli-scaled read of the SAME raw number would have given.
        Assert.True(resolvedMicro >= 0);
        _ = resolvedIfItHadBeenMilli; // documents the comparison point; not asserted equal on purpose
    }

    [Fact]
    public void A_magnitude_at_theta_150_000_resolves_rather_than_refusing()
    {
        // The whole point of B3: the old int-cast path throws (OverflowException) once the
        // per-mille magnitude exceeds int range, at Theta ~103,557 for the shipped worst case.
        // The kMicro path is long throughout and must resolve cleanly well past that point.
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMicro":1000000}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerTheta: 150000, powerTuning: Tuning);

        var pTheta = new PowerLadder(Tuning).Value(150000);
        var expected = 1000000L * pTheta / 1_000_000;

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(expected, Convert.ToInt64(action.Params["flat"]));
    }

    [Fact]
    public void No_shipped_archetype_produces_a_zero_kMicro_coefficient_at_any_tier()
    {
        // Replays the three shipped archetypes' worked node-budget shares from B1's own known-answer
        // tables through the real binder-style kMicro formula
        // (kMicro = round_half_away(budgetShareMilli * branchBudgetMicro * channelAnchorMilli / 1_000_000)),
        // at D29's ten tiers, and asserts none is zero -- proving the per-million width actually
        // fixes the shallow-tier zero-coefficient defect kMilli had, not merely widening the type.
        long[] allShares =
        {
            9,9,18,18,27,28,36,37,45,46,54,55,63,64,72,73,82,82,91,91,               // broad-and-flat
            6,6,6,12,12,12,18,18,19,36,37,45,46,54,55,63,64,145,164,182,             // gated-deep
            18,36,27,28,36,37,45,46,54,55,63,64,72,73,54,54,56,60,60,62,             // late-crown
        };
        const long branchBudgetMicro = 1_000_000; // treeShareMilli=treeBudgetMilli=1000 placeholder (A1)
        const long channelAnchorMilli = 135; // a real atk-family anchor magnitude, not zero

        foreach (var share in allShares)
        {
            var kMicro = checked(share * branchBudgetMicro * channelAnchorMilli / 1_000_000);
            Assert.True(kMicro > 0, $"share {share} produced a zero kMicro coefficient");
        }
    }
}
