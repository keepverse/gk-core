using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T6.2's second gap (`patron-absorption`, owner-approved as "a new FA1 op" 2026-09-02, then found
/// not to need one): `AuraMilli`'s flat part is `clamp(RarityBaseMilli(rarity) + PerStarMilli·star
/// + level, 0, AuraClampMilli)`. No clamp exists in the closed FA1 op vocabulary
/// (`AtomRowValidator.StatOps`/`DerivedOps`) or any channel-level policy. `level` is the only true
/// per-owner runtime input (rarity/star are fixed per authored container) and is already available
/// at compile time — the same `ownerLevel` parameter curve-scaled values already read — so this
/// resolves exactly like <see cref="PowerLadderMagnitudeTests"/>'s own `powerLadder` marker: a
/// compile-time bake, no Injector-side change, no new runtime opcode.
/// </summary>
public class ClampedLevelScaleMagnitudeTests
{
    // ---- ValueSpec.Validate() ----------------------------------------------------------------------

    [Fact]
    public void A_clampedLevelScale_spec_in_the_canonical_shape_is_valid()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed,
            ClampedLevelScale: true, ClampedLevelScaleBaseMilli: 461, ClampedLevelScaleCapMilli: 5000);

        Assert.True(spec.Validate().IsOk);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void A_clampedLevelScale_spec_carrying_a_nonzero_min_or_max_is_rejected(int min, int max)
    {
        var spec = new ValueSpec(min, max, RollPolicy.Fixed,
            ClampedLevelScale: true, ClampedLevelScaleBaseMilli: 461, ClampedLevelScaleCapMilli: 5000);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_clampedLevelScale_spec_with_a_non_fixed_roll_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.OnApply,
            ClampedLevelScale: true, ClampedLevelScaleBaseMilli: 461, ClampedLevelScaleCapMilli: 5000);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_clampedLevelScale_spec_also_carrying_a_curve_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, CurveId: "curve.x",
            ClampedLevelScale: true, ClampedLevelScaleBaseMilli: 461, ClampedLevelScaleCapMilli: 5000);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_negative_capMilli_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed,
            ClampedLevelScale: true, ClampedLevelScaleBaseMilli: 0, ClampedLevelScaleCapMilli: -1);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_non_clampedLevelScale_spec_is_completely_unaffected_by_the_new_fields()
    {
        Assert.True(new ValueSpec(10, 20, RollPolicy.OnApply).Validate().IsOk);
        Assert.False(new ValueSpec(10, 20, RollPolicy.OnApply).ClampedLevelScale);
    }

    // ---- AtomJson.TryReadValueSpec grammar ---------------------------------------------------------

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_clampedLevelScale_object_grammar_parses()
    {
        var ok = AtomJson.TryReadValueSpec(
            Json("""{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.True(spec.ClampedLevelScale);
        Assert.Equal(461, spec.ClampedLevelScaleBaseMilli);
        Assert.Equal(5000, spec.ClampedLevelScaleCapMilli);
    }

    [Fact]
    public void ClampedLevelScale_without_baseMilli_is_rejected_never_defaulted_silently()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"clampedLevelScale":true,"capMilli":5000}"""), out _);

        Assert.False(ok.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, ok.Reason);
    }

    [Fact]
    public void ClampedLevelScale_without_capMilli_is_rejected_never_defaulted_silently()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"clampedLevelScale":true,"baseMilli":461}"""), out _);

        Assert.False(ok.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, ok.Reason);
    }

    [Fact]
    public void ClampedLevelScale_false_is_rejected_rather_than_silently_falling_through()
    {
        var ok = AtomJson.TryReadValueSpec(
            Json("""{"clampedLevelScale":false,"baseMilli":461,"capMilli":5000}"""), out _);

        Assert.False(ok.IsOk);
    }

    [Fact]
    public void An_ordinary_min_max_spec_still_parses_exactly_as_before()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"min":10,"max":20,"roll":"onApply"}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.False(spec.ClampedLevelScale);
    }

    // ---- AtomRowValidator: kind restriction --------------------------------------------------------

    static AtomRow StatModifyAtom(string paramsJson, string family = "atom.aura-flat") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    static AtomRow ResourceDeltaAtom(string paramsJson, string family = "atom.badclampedlevelscale") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "resource.delta",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = $$"""{"trigger":"{{EffectTriggers.OnDamageDealt}}"}""",
    };

    [Fact]
    public void A_clampedLevelScale_amount_on_stat_modify_is_accepted_at_load()
    {
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void A_clampedLevelScale_amount_on_resource_delta_is_rejected_at_load()
    {
        var atom = ResourceDeltaAtom(
            """{"channel":"hp","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        var result = AtomRowValidator.Validate(atom, kindId => null);

        Assert.False(result.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, result.Reason);
    }

    // ---- AtomCompiler: the compile-time resolve ----------------------------------------------------

    [Fact]
    public void The_clampedLevelScale_atom_still_takes_the_compiled_path_not_the_runner()
    {
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Lawn).Path);
    }

    [Fact]
    public void Below_the_cap_the_compiler_bakes_base_plus_level_exactly()
    {
        // Might, star 3, level 100: RarityBaseMilli-shaped base 461 (matching a real
        // PatronPolicy.AuraMilli-scale constant), well under a 5000 cap.
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerLevel: 100);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(561, Convert.ToInt32(action.Params["flat"])); // 461 + 100
    }

    [Fact]
    public void At_or_above_the_cap_the_compiler_bakes_the_ceiling_not_the_raw_sum()
    {
        // The whole reason this marker exists: PatronPolicy.AuraClampMilli caps the flat part so it
        // does not grow forever with level -- past the cap, the result must stay flat at the cap.
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        var compiledAtCap = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerLevel: 4539);
        var compiledOverCap = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerLevel: 100_000);

        var atCap = Assert.Single(Assert.Single(compiledAtCap.Defs).Actions);
        var overCap = Assert.Single(Assert.Single(compiledOverCap.Defs).Actions);
        Assert.Equal(5000, Convert.ToInt32(atCap.Params["flat"]));
        Assert.Equal(5000, Convert.ToInt32(overCap.Params["flat"])); // never exceeds the cap regardless of level
    }

    [Fact]
    public void A_negative_base_clamps_to_zero_never_a_negative_flat()
    {
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":-500,"capMilli":5000}}""");

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerLevel: 1);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(0, Convert.ToInt32(action.Params["flat"])); // -500 + 1 = -499, clamped to 0
    }

    [Fact]
    public void The_default_ownerLevel_of_one_is_used_when_the_caller_never_sets_it()
    {
        // Unlike powerLadder's Θ, ownerLevel is the SAME pre-existing non-nullable parameter every
        // curve-scaled value already reads (defaults to 1) -- no "missing context" throw exists or
        // is needed here, proven directly rather than assumed.
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":10,"capMilli":5000}}""");

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(11, Convert.ToInt32(action.Params["flat"])); // 10 + ownerLevel's own default of 1
    }

    [Fact]
    public void The_compiler_bakes_a_plain_number_never_a_marker()
    {
        var atom = StatModifyAtom(
            """{"channel":"atk","op":"flat","amount":{"clampedLevelScale":true,"baseMilli":461,"capMilli":5000}}""");

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerLevel: 10);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.IsNotType<Dictionary<string, object?>>(action.Params["flat"]);
    }
}
