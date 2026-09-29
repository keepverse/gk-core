using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// T6.2 (`patron-absorption`, `tasks/seed-to-concrete-open-decisions.md` §2, owner-approved
/// 2026-09-02): the same "a magnitude needs something outside <see cref="ValueSpec.Resolve"/>'s own
/// scope" shape `eventField` already solved for firing-event fields
/// (<see cref="EventLinkedMagnitudeTests"/>), applied here to an owner's own power index — read
/// <c>PowerLadder.Value(Θ)</c> instead of rolling Min/Max. Unlike `eventField`, Θ is known at
/// COMPILE time (an owner's own power index, not something a hit produces), so this resolves
/// directly in <see cref="AtomCompiler"/> — no marker, no deferred runtime consumer.
/// </summary>
public class PowerLadderMagnitudeTests
{
    static readonly PowerTuning Tuning = PowerTuning.Build(
        1, 1, PowerTuning.FixedCMilli, 400, PowerTuning.FixedPinIndex, PowerTuning.FixedPinValue,
        1000, 25000, 250, 1000, 5000, 5000, 25000);

    // ---- ValueSpec.Validate() ----------------------------------------------------------------------

    [Fact]
    public void A_powerLadder_spec_in_the_canonical_shape_is_valid()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, PowerLadder: true, PowerLadderKMilli: 130);

        Assert.True(spec.Validate().IsOk);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void A_powerLadder_spec_carrying_a_nonzero_min_or_max_is_rejected(int min, int max)
    {
        var spec = new ValueSpec(min, max, RollPolicy.Fixed, PowerLadder: true, PowerLadderKMilli: 130);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_powerLadder_spec_with_a_non_fixed_roll_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.OnApply, PowerLadder: true, PowerLadderKMilli: 130);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_powerLadder_spec_also_carrying_a_curve_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, CurveId: "curve.x", PowerLadder: true, PowerLadderKMilli: 130);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_non_powerLadder_spec_is_completely_unaffected_by_the_new_fields()
    {
        Assert.True(new ValueSpec(10, 20, RollPolicy.OnApply).Validate().IsOk);
        Assert.False(new ValueSpec(10, 20, RollPolicy.OnApply).PowerLadder);
        Assert.Equal(0, new ValueSpec(10, 20, RollPolicy.OnApply).PowerLadderKMilli);
    }

    // ---- AtomJson.TryReadValueSpec grammar ---------------------------------------------------------

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_powerLadder_object_grammar_parses()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"powerLadder":true,"kMilli":130}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.True(spec.PowerLadder);
        Assert.Equal(130, spec.PowerLadderKMilli);
    }

    [Fact]
    public void PowerLadder_without_an_explicit_kMilli_is_rejected_never_defaulted_silently()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"powerLadder":true}"""), out _);

        Assert.False(ok.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, ok.Reason);
    }

    [Fact]
    public void PowerLadder_false_is_rejected_rather_than_silently_falling_through()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"powerLadder":false,"kMilli":130}"""), out _);

        Assert.False(ok.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, ok.Reason);
    }

    [Fact]
    public void A_non_bool_powerLadder_is_rejected()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"powerLadder":"yes","kMilli":130}"""), out _);

        Assert.False(ok.IsOk);
    }

    [Fact]
    public void An_ordinary_min_max_spec_still_parses_exactly_as_before()
    {
        // The new branch is checked right after eventField -- prove it never intercepts the
        // pre-existing grammar for a spec with no "powerLadder" key.
        var ok = AtomJson.TryReadValueSpec(Json("""{"min":10,"max":20,"roll":"onApply"}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.False(spec.PowerLadder);
    }

    // ---- AtomRowValidator: kind restriction --------------------------------------------------------

    static AtomRow StatModifyAtom(string paramsJson, string family = "atom.aura") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    static AtomRow StatDerivedAtom(string paramsJson, string family = "atom.aura-derived") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.derived",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    static AtomRow ResourceDeltaAtom(string paramsJson, string family = "atom.badpowerladder") => new()
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
    public void A_powerLadder_amount_on_stat_modify_is_accepted_at_load()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void A_powerLadder_amount_on_stat_derived_is_accepted_at_load()
    {
        // channel is progression.bonus.defense, not the .atk sibling this test used before: .atk was
        // retired and unregistered by solid-enforcement retire-atk (R4, 2026-09-18); the swap keeps
        // the same bridge-channel family and shape, since this test's point is the powerLadder marker
        // on a stat.derived atom, not which of the five progression.bonus.* channels carries it.
        var atom = StatDerivedAtom(
            """{"channel":"progression.bonus.defense","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void A_powerLadder_amount_on_resource_delta_is_rejected_at_load()
    {
        // Scoped to stat.modify/stat.derived only -- the kinds the migration actually needs, so a
        // resolved number never silently reaches a triggered runner atom with no owner Θ in scope.
        var atom = ResourceDeltaAtom("""{"channel":"hp","amount":{"powerLadder":true,"kMilli":130}}""");

        var result = AtomRowValidator.Validate(atom, kindId => null);

        Assert.False(result.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, result.Reason);
    }

    // ---- AtomCompiler: the compile-time resolve --------------------------------------------------

    [Fact]
    public void The_powerLadder_atom_still_takes_the_compiled_path_not_the_runner()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Lawn).Path);
    }

    [Fact]
    public void The_compiler_resolves_powerLadder_to_kMilli_times_PowerLadder_Value_over_1000()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            ownerTheta: 250, powerTuning: Tuning);

        var expectedPTheta = new PowerLadder(Tuning).Value(250);
        var expected = (int)(130L * expectedPTheta / 1000);

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);
        // stat.modify with op:flat bakes straight to the opcode's own "flat" key (ToOpcodeShape).
        Assert.Equal(expected, Convert.ToInt32(action.Params["flat"]));
    }

    [Fact]
    public void The_compiler_bakes_a_plain_number_never_a_marker_because_theta_is_known_at_compile_time()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            ownerTheta: 250, powerTuning: Tuning);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.IsNotType<Dictionary<string, object?>>(action.Params["flat"]);
    }

    [Fact]
    public void Compiling_a_powerLadder_atom_with_no_ownerTheta_throws_rather_than_pricing_it_at_zero()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        Assert.Throws<InvalidOperationException>(() =>
            AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, powerTuning: Tuning));
    }

    [Fact]
    public void Compiling_a_powerLadder_atom_with_no_powerTuning_throws_rather_than_pricing_it_at_zero()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        Assert.Throws<InvalidOperationException>(() =>
            AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1, ownerTheta: 250));
    }

    [Fact]
    public void A_zero_theta_resolves_to_zero_not_a_crash()
    {
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"powerLadder":true,"kMilli":130}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            ownerTheta: 0, powerTuning: Tuning);

        var expectedPTheta = new PowerLadder(Tuning).Value(0);
        var expected = (int)(130L * expectedPTheta / 1000);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.Equal(expected, Convert.ToInt32(action.Params["flat"]));
    }
}
