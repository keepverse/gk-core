using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

/// <summary>
/// passive-tree `element-conversion` (D56, spec-element-conversion.md §4). Follows
/// <c>AtomKindRegistryTests</c>'s own shape (self-consistency counts) for the vocabulary-size checks
/// (in <c>AtomKindRegistryTests.cs</c> itself) and adds this dedicated suite for
/// <see cref="ElementConversion.Apply"/>'s own composition rules.
/// </summary>
public class ElementConversionTests
{
    static ElementPayload Payload(params (ElementTypeId Element, double Weight)[] components) =>
        ElementPayload.From(components.Select(c => new ElementPayloadComponent(c.Element, c.Weight)).ToList());

    // ---- §4 test 1: the closed vocabulary reports 9/18 (self-consistency, lives in AtomKindRegistryTests) ----

    [Fact]
    public void Element_convert_is_registered_on_the_Element_attach_point()
    {
        var kind = AtomKindRegistry.Get("element.convert")!;
        Assert.NotNull(kind);
        Assert.Equal(AttachPoint.Element, kind.Attach);
        Assert.Empty(kind.Triggers); // permanent modifier, no trigger allowed
    }

    // ---- §4 test 2: 400‰ worked example, exact ------------------------------------------------------

    [Fact]
    public void A_400_permille_conversion_produces_the_spec_own_exact_worked_example()
    {
        var payload = Payload((ElementTypeId.Fire, 1.0));
        var result = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 400)!;

        Assert.Equal(2, result.Components.Count);
        Assert.Equal(0.6, result.Components.Single(c => c.Element == ElementTypeId.Fire).Weight, precision: 10);
        Assert.Equal(0.4, result.Components.Single(c => c.Element == ElementTypeId.Ice).Weight, precision: 10);
    }

    // ---- §4 test 3: full (1000‰) conversion removes the source, never retains it at zero -------------

    [Fact]
    public void A_full_conversion_removes_the_source_component_rather_than_retaining_it_at_zero()
    {
        var payload = Payload((ElementTypeId.Fire, 1.0));
        var result = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 1000)!;

        var component = Assert.Single(result.Components);
        Assert.Equal(ElementTypeId.Ice, component.Element);
        Assert.Equal(1.0, component.Weight, precision: 10);
        // ElementPayload.Validate (called inside Apply, via ElementPayload.From) would have thrown had
        // Fire survived at weight 0 -- reaching this line at all is part of the proof.
    }

    // ---- §4 test 4: a null payload is a no-op, never fabricated -----------------------------------

    [Fact]
    public void A_null_payload_is_a_no_op_never_fabricated_never_an_error()
    {
        var result = ElementConversion.Apply(null, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 400);
        Assert.Null(result);
    }

    [Fact]
    public void A_named_fromElement_absent_from_the_payload_is_a_no_op_for_that_atom()
    {
        var payload = Payload((ElementTypeId.Ice, 1.0));
        var result = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Dark, shareMilli: 400);

        // Nothing named "Fire" exists to convert from -- the payload passes through unchanged, never a
        // fabricated Fire component and never an exception.
        Assert.Same(payload, result);
    }

    // ---- §4 test 5: two stacked conversions on the same fromElement never exceed its own weight -----

    [Fact]
    public void Two_conversions_on_the_same_fromElement_never_move_more_than_its_own_current_weight()
    {
        var payload = Payload((ElementTypeId.Fire, 1.0));

        // Forward order: 600‰ then another 600‰ of what's left.
        var afterFirst = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 600)!;
        var afterSecond = ElementConversion.Apply(afterFirst, ElementTypeId.Fire, ElementTypeId.Dark, shareMilli: 600)!;

        var fire = afterSecond.Components.SingleOrDefault(c => c.Element == ElementTypeId.Fire);
        var ice = afterSecond.Components.Single(c => c.Element == ElementTypeId.Ice);
        var dark = afterSecond.Components.Single(c => c.Element == ElementTypeId.Dark);

        // First: Fire 1.0 -> Fire 0.4, Ice 0.6. Second (600‰ of the REMAINING 0.4, not the original
        // 1.0): Fire 0.4 -> Fire 0.16, Dark 0.24. Total moved (0.6 + 0.24 = 0.84) is less than Fire's
        // original 1.0 -- the second call read the CURRENT weight, never the original.
        Assert.NotNull(fire);
        Assert.Equal(0.16, fire!.Weight, precision: 10);
        Assert.Equal(0.6, ice.Weight, precision: 10);
        Assert.Equal(0.24, dark.Weight, precision: 10);

        var sum = afterSecond.Components.Sum(c => c.Weight);
        Assert.Equal(1.0, sum, precision: 9);
    }

    [Fact]
    public void An_adversarial_reverse_ordering_never_produces_a_negative_or_over_budget_weight()
    {
        // Two nodes both claim to convert 100% of the SAME fromElement to two different targets.
        // Whichever applies second must find nothing left (a no-op for it), not a negative weight.
        var payload = Payload((ElementTypeId.Fire, 1.0));

        var order1 = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 1000)!;
        order1 = ElementConversion.Apply(order1, ElementTypeId.Fire, ElementTypeId.Dark, shareMilli: 1000)!;

        // Fire was fully converted to Ice by the first atom; the second finds no Fire component at all
        // and is correctly a no-op (source not present).
        Assert.DoesNotContain(order1.Components, c => c.Weight < 0);
        Assert.DoesNotContain(order1.Components, c => c.Element == ElementTypeId.Fire);
        Assert.Equal(1.0, order1.Components.Sum(c => c.Weight), precision: 9);
    }

    // ---- omitted fromElement: the largest remaining component (§2c) ---------------------------------

    [Fact]
    public void An_omitted_fromElement_converts_the_single_largest_remaining_component()
    {
        var payload = Payload((ElementTypeId.Fire, 0.3), (ElementTypeId.Ice, 0.7));
        var result = ElementConversion.Apply(payload, fromElement: null, ElementTypeId.Dark, shareMilli: 500)!;

        // Ice (0.7) is the largest -- 500‰ of it (0.35) moves to Dark.
        Assert.Equal(0.3, result.Components.Single(c => c.Element == ElementTypeId.Fire).Weight, precision: 10);
        Assert.Equal(0.35, result.Components.Single(c => c.Element == ElementTypeId.Ice).Weight, precision: 10);
        Assert.Equal(0.35, result.Components.Single(c => c.Element == ElementTypeId.Dark).Weight, precision: 10);
    }

    // ---- §4 test 6: no trigger allowed --------------------------------------------------------------

    [Fact]
    public void Element_convert_carries_no_trigger_and_a_supplied_one_is_refused()
    {
        var rejection = AtomKindRegistry.ValidateTrigger("element.convert", AtomTriggers.OnDamageDealt);
        Assert.False(rejection.IsOk);
    }

    // ---- shareMilli bounds ----------------------------------------------------------------------

    [Theory]
    [InlineData(0L)]
    [InlineData(1001L)]
    [InlineData(-1L)]
    public void ShareMilli_outside_1_to_1000_is_refused(long shareMilli)
    {
        var payload = Payload((ElementTypeId.Fire, 1.0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli));
    }

    // ---- converting to an element already present merges into its existing weight -------------------

    [Fact]
    public void Converting_into_an_element_already_present_adds_to_its_existing_weight_not_a_second_row()
    {
        var payload = Payload((ElementTypeId.Fire, 0.5), (ElementTypeId.Ice, 0.5));
        var result = ElementConversion.Apply(payload, ElementTypeId.Fire, ElementTypeId.Ice, shareMilli: 1000)!;

        var component = Assert.Single(result.Components);
        Assert.Equal(ElementTypeId.Ice, component.Element);
        Assert.Equal(1.0, component.Weight, precision: 10);
    }
}
