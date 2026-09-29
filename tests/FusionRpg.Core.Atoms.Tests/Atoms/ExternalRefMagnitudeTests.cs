using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// `patron-absorption` (`spec-patron-absorption.md`'s 2026-09-06 amendment): the "referenced, not
/// re-expressed" shape that spec decided on — some magnitudes are not safely reproducible as an atom
/// formula because the real value is per-player-varying and already computed by a shipped function
/// elsewhere. Unlike <see cref="PowerLadderMagnitudeTests"/>/<see cref="ClampedLevelScaleMagnitudeTests"/>
/// (which bake a number from compile-time context <see cref="AtomCompiler"/> already has),
/// <c>externalRef</c> resolves via a caller-supplied callback — the compiler never learns what the
/// ref id means, matching how <c>curves: Func&lt;string, CurveTable?&gt;</c> already works.
/// </summary>
public class ExternalRefMagnitudeTests
{
    // ---- ValueSpec.Validate() ----------------------------------------------------------------------

    [Fact]
    public void An_externalRef_spec_in_the_canonical_shape_is_valid()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, ExternalRef: "patron.auraMilli");

        Assert.True(spec.Validate().IsOk);
    }

    [Fact]
    public void An_empty_externalRef_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, ExternalRef: "");

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void An_externalRef_spec_carrying_a_nonzero_min_or_max_is_rejected(int min, int max)
    {
        var spec = new ValueSpec(min, max, RollPolicy.Fixed, ExternalRef: "patron.auraMilli");

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void An_externalRef_spec_with_a_non_fixed_roll_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.OnApply, ExternalRef: "patron.auraMilli");

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void An_externalRef_spec_also_carrying_a_curve_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, CurveId: "curve.x", ExternalRef: "patron.auraMilli");

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_non_externalRef_spec_is_completely_unaffected_by_the_new_field()
    {
        Assert.True(new ValueSpec(10, 20, RollPolicy.OnApply).Validate().IsOk);
        Assert.Null(new ValueSpec(10, 20, RollPolicy.OnApply).ExternalRef);
    }

    // ---- AtomJson.TryReadValueSpec grammar ---------------------------------------------------------

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_externalRef_object_grammar_parses()
    {
        var el = Json("""{"externalRef": "patron.auraMilli"}""");

        var check = AtomJson.TryReadValueSpec(el, out var spec);

        Assert.True(check.IsOk);
        Assert.Equal("patron.auraMilli", spec.ExternalRef);
    }

    [Fact]
    public void ExternalRef_with_a_non_string_value_is_rejected()
    {
        var el = Json("""{"externalRef": 123}""");

        var check = AtomJson.TryReadValueSpec(el, out _);

        Assert.False(check.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, check.Reason);
    }

    [Fact]
    public void ExternalRef_with_an_empty_string_is_rejected_rather_than_silently_accepted()
    {
        var el = Json("""{"externalRef": ""}""");

        var check = AtomJson.TryReadValueSpec(el, out _);

        Assert.False(check.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, check.Reason);
    }

    [Fact]
    public void An_ordinary_min_max_spec_still_parses_exactly_as_before()
    {
        var el = Json("""{"min": 5, "max": 10, "roll": "onApply"}""");

        var check = AtomJson.TryReadValueSpec(el, out var spec);

        Assert.True(check.IsOk);
        Assert.Null(spec.ExternalRef);
        Assert.Equal(5, spec.Min);
        Assert.Equal(10, spec.Max);
    }

    // ---- AtomRowValidator: kind restriction --------------------------------------------------------

    static AtomRow StatModifyAtom(string paramsJson, string family = "atom.patron-aura") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    static AtomRow StatDerivedAtom(string paramsJson, string family = "atom.patron-aura-derived") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.derived",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    static AtomRow ResourceDeltaAtom(string paramsJson, string family = "atom.badexternalref") => new()
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
    public void An_externalRef_amount_on_stat_modify_is_accepted_at_load()
    {
        // A plain stat channel, matching PowerLadderMagnitudeTests' own working example — this test
        // is about externalRef being legal on stat.modify, not about combat.power.* specifically
        // (which stat.modify itself does not accept; that is a derived channel, stat.derived's own).
        var atom = StatModifyAtom("""{"channel":"atk","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void An_externalRef_amount_on_stat_derived_is_accepted_at_load()
    {
        var atom = StatDerivedAtom(
            """{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void An_externalRef_amount_on_resource_delta_is_rejected_at_load()
    {
        var atom = ResourceDeltaAtom("""{"channel":"hp","amount":{"externalRef":"patron.auraMilli"}}""");

        var result = AtomRowValidator.Validate(atom, kindId => null);

        Assert.False(result.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, result.Reason);
    }

    // ---- AtomCompiler: the compile-time resolve via callback ---------------------------------------

    [Fact]
    public void The_externalRef_atom_still_takes_the_compiled_path_not_the_runner()
    {
        var atom = StatModifyAtom("""{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Lawn).Path);
    }

    [Fact]
    public void The_compiler_resolves_externalRef_by_invoking_the_supplied_callback()
    {
        var atom = StatModifyAtom("""{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            externalRefs: id => id == "patron.auraMilli" ? 137 : throw new InvalidOperationException("unexpected id " + id));

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);
        Assert.Equal(137, Convert.ToInt32(action.Params["flat"]));
    }

    [Fact]
    public void The_compiler_bakes_a_plain_number_never_a_marker()
    {
        var atom = StatModifyAtom("""{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        var compiled = AtomCompiler.Compile(
            new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
            externalRefs: _ => 42);

        var action = Assert.Single(Assert.Single(compiled.Defs).Actions);
        Assert.IsNotType<Dictionary<string, object?>>(action.Params["flat"]);
    }

    [Fact]
    public void Compiling_an_externalRef_atom_with_no_callback_supplied_throws_naming_the_ref_id()
    {
        var atom = StatModifyAtom("""{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1));

        Assert.Contains("patron.auraMilli", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_callback_that_itself_refuses_an_unknown_id_propagates_that_refusal()
    {
        // AtomCompiler never validates the id itself — an unrecognised ref is the CALLBACK's own
        // refusal to make (it owns the registry), not something this layer second-guesses.
        var atom = StatModifyAtom("""{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"no.such.ref"}}""");

        Assert.Throws<InvalidOperationException>(() =>
            AtomCompiler.Compile(
                new[] { atom }, RuntimeId.Lawn, catalogRevision: 1,
                externalRefs: id => throw new InvalidOperationException("unknown externalRef id: " + id)));
    }

    [Fact]
    public void Two_atoms_with_different_externalRef_ids_each_resolve_independently()
    {
        var atomA = StatModifyAtom(
            """{"channel":"combat.power.fire","op":"flat","amount":{"externalRef":"patron.auraMilli"}}""",
            family: "atom.patron-aura-a");
        var atomB = StatModifyAtom(
            """{"channel":"combat.defense.fire","op":"flat","amount":{"externalRef":"patron.auraDefenseMilli"}}""",
            family: "atom.patron-aura-b");

        var calls = new List<string>();
        var compiled = AtomCompiler.Compile(
            new[] { atomA, atomB }, RuntimeId.Lawn, catalogRevision: 1,
            externalRefs: id =>
            {
                calls.Add(id);
                return id == "patron.auraMilli" ? 100 : 50;
            });

        Assert.Equal(2, compiled.Defs.Count);
        Assert.Contains("patron.auraMilli", calls);
        Assert.Contains("patron.auraDefenseMilli", calls);
    }
}
