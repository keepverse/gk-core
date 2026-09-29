using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// `P0.2` (spec-value-spec-and-curve.md "Event-linked magnitudes", action-ideal.md §8.5 — landed
/// 2026-08-28): GAS's `SetByCaller` shape — a magnitude that reads a field the FIRING event already
/// carries (<c>ev.Damage</c>) instead of rolling Min/Max. Covers the whole chain: the JSON grammar, the
/// `ValueSpec` shape validation, the kind restriction, the compile-time marker bake, the fire-time
/// resolution, and one end-to-end lifesteal chain through the real <see cref="EffectBag"/>.
/// </summary>
public class EventLinkedMagnitudeTests
{
    // ---- ValueSpec.Validate() --------------------------------------------------------------------

    [Fact]
    public void An_eventField_spec_in_the_canonical_shape_is_valid()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, EventField: "damage", MultiplierMilli: 500);

        Assert.True(spec.Validate().IsOk);
    }

    [Fact]
    public void An_unknown_eventField_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, EventField: "killerPtr", MultiplierMilli: 1000);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public void An_eventField_spec_carrying_a_nonzero_min_or_max_is_rejected(int min, int max)
    {
        var spec = new ValueSpec(min, max, RollPolicy.Fixed, EventField: "damage", MultiplierMilli: 500);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void An_eventField_spec_with_a_non_fixed_roll_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.OnApply, EventField: "damage", MultiplierMilli: 500);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void An_eventField_spec_also_carrying_a_curve_is_rejected()
    {
        var spec = new ValueSpec(0, 0, RollPolicy.Fixed, CurveId: "curve.x", EventField: "damage", MultiplierMilli: 500);

        Assert.Equal(AtomRejectionReason.BadValueSpec, spec.Validate().Reason);
    }

    [Fact]
    public void A_non_eventField_spec_is_completely_unaffected_by_the_new_fields()
    {
        // Backward compatibility: every pre-existing ValueSpec construction leaves EventField/
        // MultiplierMilli at their defaults, and validation must behave exactly as before.
        Assert.True(new ValueSpec(10, 20, RollPolicy.OnApply).Validate().IsOk);
        Assert.Equal(1000, new ValueSpec(10, 20, RollPolicy.OnApply).MultiplierMilli);
        Assert.Null(new ValueSpec(10, 20, RollPolicy.OnApply).EventField);
    }

    // ---- AtomJson.TryReadValueSpec grammar -------------------------------------------------------

    static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void The_eventField_object_grammar_parses()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"eventField":"damage","multiplierMilli":500}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.Equal("damage", spec.EventField);
        Assert.Equal(500, spec.MultiplierMilli);
    }

    [Fact]
    public void EventField_without_an_explicit_multiplierMilli_is_rejected_never_defaulted_silently()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"eventField":"damage"}"""), out _);

        Assert.False(ok.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, ok.Reason);
    }

    [Fact]
    public void A_non_string_eventField_is_rejected()
    {
        var ok = AtomJson.TryReadValueSpec(Json("""{"eventField":42,"multiplierMilli":500}"""), out _);

        Assert.False(ok.IsOk);
    }

    [Fact]
    public void An_ordinary_min_max_spec_still_parses_exactly_as_before()
    {
        // The new branch is checked FIRST in TryReadValueSpec -- prove it never intercepts the
        // pre-existing grammar for a spec with no "eventField" key.
        var ok = AtomJson.TryReadValueSpec(Json("""{"min":10,"max":20,"roll":"onApply"}"""), out var spec);

        Assert.True(ok.IsOk);
        Assert.Equal(10, spec.Min);
        Assert.Equal(20, spec.Max);
        Assert.Null(spec.EventField);
    }

    [Fact]
    public void A_bare_number_still_parses_exactly_as_before()
    {
        var ok = AtomJson.TryReadValueSpec(Json("45"), out var spec);

        Assert.True(ok.IsOk);
        Assert.Equal(ValueSpec.Of(45), spec);
    }

    // ---- AtomRowValidator: kind restriction -------------------------------------------------------

    static AtomRow ResourceDeltaAtom(string paramsJson, string family = "atom.lifesteal") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "resource.delta",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = $$"""{"trigger":"{{EffectTriggers.OnDamageDealt}}"}""",
    };

    static AtomRow StatModifyAtom(string paramsJson, string family = "atom.badeventfield") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = "stat.modify",
        FamilyId = family,
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = "{}",
    };

    [Fact]
    public void An_eventField_amount_on_resource_delta_is_accepted_at_load()
    {
        var atom = ResourceDeltaAtom("""{"channel":"hp","amount":{"eventField":"damage","multiplierMilli":500}}""");

        Assert.True(AtomRowValidator.Validate(atom, kindId => null).IsOk);
    }

    [Fact]
    public void An_eventField_amount_on_stat_modify_is_rejected_at_load()
    {
        // Scoped to resource.delta only -- the kind lifesteal/Corrosion content actually needs, so a
        // marker never reaches a sink (FA1's stat writer) with no idea how to unwrap it.
        var atom = StatModifyAtom(
            """{"channel":"maxHp","op":"flat","amount":{"eventField":"damage","multiplierMilli":500}}""");

        var result = AtomRowValidator.Validate(atom, kindId => null);

        Assert.False(result.IsOk);
        Assert.Equal(AtomRejectionReason.BadValueSpec, result.Reason);
    }

    // ---- AtomCompiler: the compile-time marker bake ------------------------------------------------

    [Fact]
    public void The_compiler_bakes_a_marker_object_instead_of_a_literal_for_an_eventField_spec()
    {
        var atom = ResourceDeltaAtom("""{"channel":"hp","amount":{"eventField":"damage","multiplierMilli":500}}""");

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);

        var def = Assert.Single(compiled.Defs);
        var action = Assert.Single(def.Actions);
        var marker = Assert.IsType<Dictionary<string, object?>>(action.Params["amount"]);
        Assert.Equal("damage", marker["eventField"]);
        Assert.Equal(500, marker["multiplierMilli"]);
    }

    [Fact]
    public void An_eventField_atom_still_takes_the_compiled_path_not_the_runner()
    {
        var atom = ResourceDeltaAtom("""{"channel":"hp","amount":{"eventField":"damage","multiplierMilli":500}}""");

        Assert.Equal(AtomPath.Compiled, Compilability.Classify(atom, RuntimeId.Lawn).Path);
    }

    // ---- DamagePacketBuilder: fire-time resolution -------------------------------------------------

    static Dictionary<string, object?> MarkerOverlay(string eventField, int multiplierMilli) => new()
    {
        ["channel"] = "hp",
        ["amount"] = new Dictionary<string, object?> { ["eventField"] = eventField, ["multiplierMilli"] = multiplierMilli },
    };

    [Fact]
    public void FromOverlay_resolves_half_of_the_events_damage()
    {
        var packet = DamagePacketBuilder.FromOverlay(
            MarkerOverlay("damage", 500), new EffectEventDto { Damage = 100 });

        Assert.Equal(50, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_rounds_half_away_from_zero()
    {
        // 33 x 500 / 1000 = 16.5 -> 17.
        var packet = DamagePacketBuilder.FromOverlay(
            MarkerOverlay("damage", 500), new EffectEventDto { Damage = 33 });

        Assert.Equal(17, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_with_a_full_1000_multiplier_returns_the_damage_unchanged()
    {
        var packet = DamagePacketBuilder.FromOverlay(
            MarkerOverlay("damage", 1000), new EffectEventDto { Damage = 777 });

        Assert.Equal(777, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_with_no_firing_event_resolves_to_zero_rather_than_throwing()
    {
        var packet = DamagePacketBuilder.FromOverlay(MarkerOverlay("damage", 500), ev: null);

        Assert.Equal(0, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_with_an_event_carrying_no_damage_resolves_to_zero()
    {
        var packet = DamagePacketBuilder.FromOverlay(
            MarkerOverlay("damage", 500), new EffectEventDto { Damage = null });

        Assert.Equal(0, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_still_reads_a_plain_number_exactly_as_before()
    {
        var packet = DamagePacketBuilder.FromOverlay(
            new Dictionary<string, object?> { ["amount"] = -50L }, new EffectEventDto());

        Assert.Equal(-50, packet.SignedAmount);
    }

    [Fact]
    public void FromOverlay_resolves_a_marker_that_survived_a_json_round_trip_as_a_JsonElement()
    {
        // AtomCompiler's marker is a plain Dictionary in-memory, but after a real JSON round trip
        // (e.g. through the E19 push pipeline) it comes back as a JsonElement object instead --
        // JsonOverlay.FromObject normalises both the same way every other overlay value already does.
        var overlayJson = """{"channel":"hp","amount":{"eventField":"damage","multiplierMilli":500}}""";
        using var doc = JsonDocument.Parse(overlayJson);
        var overlay = JsonOverlay.FromObject(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(doc.RootElement.GetRawText()));

        var packet = DamagePacketBuilder.FromOverlay(overlay, new EffectEventDto { Damage = 200 });

        Assert.Equal(100, packet.SignedAmount);
    }

    // ---- End to end: the lifesteal chain through the real EffectBag --------------------------------

    [Fact]
    public void A_lifesteal_grant_heals_half_the_damage_the_synthesized_OnDamageDealt_event_carries()
    {
        var h = new FoundationHarness();
        h.WithCatalog(new[]
        {
            new EffectDef
            {
                EffectId = "fx.test_attack",
                Triggers = new List<string> { EffectTriggers.OnDamageDealt },
                Actions = new List<EffectActionRow>
                {
                    new()
                    {
                        Seq = 0,
                        Action = EffectActions.ApplyResourceDelta,
                        Params = new Dictionary<string, object?> { ["channel"] = "hp", ["amount"] = -100L },
                    },
                },
            },
            new EffectDef
            {
                EffectId = "fx.test_lifesteal",
                Triggers = new List<string> { EffectTriggers.OnDamageDealt },
                Actions = new List<EffectActionRow>
                {
                    new()
                    {
                        Seq = 0,
                        Action = EffectActions.ApplyResourceDelta,
                        Params = new Dictionary<string, object?>
                        {
                            ["channel"] = "hp",
                            ["amount"] = new Dictionary<string, object?>
                            {
                                ["eventField"] = "damage",
                                ["multiplierMilli"] = 500,
                            },
                        },
                    },
                },
            },
        });
        h.SetBoard(new[]
        {
            new BoardEntitySnap { Ptr = "Z1", Side = "zombie", TypeId = 0, Col = 7, Row = 2 },
        });

        // procDepthLimit: 1 on the attack means it does not re-fire on the synthesized echo of its
        // own damage -- the chain terminates after exactly one bounce, matching the existing
        // Overlay_proc_respects_proc_depth_on_second_grant guard's own pattern.
        h.Grant(new EffectGrantDto
        {
            GrantId = "attack",
            EffectId = "fx.test_attack",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0, ["procDepthLimit"] = 1 },
        });
        h.Grant(new EffectGrantDto
        {
            GrantId = "lifesteal",
            EffectId = "fx.test_lifesteal",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?> { ["icd_ms"] = 0 },
        });

        h.OnEvent(new EffectEventDto
        {
            Trigger = EffectTriggers.OnDamageDealt,
            ActorPtr = "P1",
            TargetPtr = "Z1",
            Side = "plant",
        });

        // The synthesized echo (Damage = 100, from "attack"'s own -100 hp delta) drives "lifesteal" to
        // heal exactly 50 -- the doc's own worked example ("heal for 50% of the damage this attack
        // dealt") proven through the real runtime's own resolved plan item, not asserted by inspection.
        var lifestealAction = Assert.Single(h.Sink.Items, i => string.Equals(i.GrantId, "lifesteal", StringComparison.Ordinal));
        Assert.Equal(50, Convert.ToInt64(lifestealAction.Params["amount"]));
        Assert.Contains(h.Sink.Fired, f =>
            string.Equals(f.EffectId, "fx.test_lifesteal", StringComparison.Ordinal) && f.Ok);
    }
}
