using FusionRpg.Core.World.Ai.Utility;
using Xunit;

namespace FusionRpg.Core.Tests.World.Ai;

/// <summary>
/// W34 (spec-ai-commander.md §The consideration arithmetic): the scorer, built before anything
/// scores.
///
/// Pure integer arithmetic with no world knowledge, so it can be proven in isolation. Wave 3 then
/// inherits a tested scorer and only has to choose *which* considerations to write — the part that
/// needs an economy to argue with.
/// </summary>
public class ConsiderationTests
{
    static int Curve(ResponseCurve curve, int input) => ResponseCurves.Evaluate(curve, input);

    // ---- the curves --------------------------------------------------------------------------

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Quadratic)]
    [InlineData(ResponseCurve.InverseQuadratic)]
    [InlineData(ResponseCurve.Smoothstep)]
    public void A_rising_curve_starts_at_nothing_and_ends_at_everything(ResponseCurve curve)
    {
        Assert.Equal(0, Curve(curve, 0));
        Assert.Equal(1000, Curve(curve, 1000));
    }

    [Theory]
    [InlineData(ResponseCurve.Linear)]
    [InlineData(ResponseCurve.Quadratic)]
    [InlineData(ResponseCurve.InverseQuadratic)]
    [InlineData(ResponseCurve.Smoothstep)]
    public void A_rising_curve_never_falls(ResponseCurve curve)
    {
        // Monotonicity is the property every consideration silently depends on: a curve that dipped
        // would make "more of this" occasionally mean "care less", and the behaviour would flicker.
        var previous = -1;
        for (var x = 0; x <= 1000; x += 10)
        {
            var y = Curve(curve, x);
            Assert.True(y >= previous, $"{curve} fell at {x}: {y} after {previous}");
            previous = y;
        }
    }

    [Fact]
    public void Inverse_is_the_same_curve_read_backwards()
    {
        for (var x = 0; x <= 1000; x += 100)
            Assert.Equal(1000 - x, Curve(ResponseCurve.Inverse, x));
    }

    [Fact]
    public void Quadratic_is_slow_at_the_bottom_and_inverse_quadratic_is_urgent_at_it()
    {
        // The two shapes that matter: "I do not care until it is serious" and "any at all matters".
        Assert.True(Curve(ResponseCurve.Quadratic, 300) < 300);
        Assert.True(Curve(ResponseCurve.InverseQuadratic, 300) > 300);
    }

    [Fact]
    public void Smoothstep_is_flat_at_both_ends_and_steep_in_the_middle()
    {
        Assert.True(Curve(ResponseCurve.Smoothstep, 100) < 100);
        Assert.Equal(500, Curve(ResponseCurve.Smoothstep, 500));
        Assert.True(Curve(ResponseCurve.Smoothstep, 900) > 900);
    }

    [Fact]
    public void A_threshold_is_all_or_nothing_at_the_point_you_name()
    {
        Assert.Equal(0, ResponseCurves.Evaluate(ResponseCurve.Threshold, 699, threshold: 700));
        Assert.Equal(1000, ResponseCurves.Evaluate(ResponseCurve.Threshold, 700, threshold: 700));
    }

    [Fact]
    public void An_input_outside_the_range_is_clamped_rather_than_believed()
    {
        // A caller that normalised badly gets a sane answer instead of a score above the ceiling
        // that then wins every comparison it is in.
        Assert.Equal(0, Curve(ResponseCurve.Linear, -500));
        Assert.Equal(1000, Curve(ResponseCurve.Linear, 5000));
    }

    // ---- the product -------------------------------------------------------------------------

    [Fact]
    public void One_consideration_at_zero_kills_the_behaviour_outright()
    {
        // The whole reason the score is a product: "do not charge if you are dying" stops being an
        // `if` somebody has to remember and becomes an axis that reaches zero.
        var score = Considerations.Score(new[]
        {
            new Consideration("opportunity", ResponseCurve.Linear, 1000),
            new Consideration("health", ResponseCurve.Linear, 0)
        });

        Assert.Equal(0, score);
    }

    [Fact]
    public void Nothing_to_consider_is_not_a_reason_to_act()
    {
        Assert.Equal(0, Considerations.Score(Array.Empty<Consideration>()));
    }

    [Fact]
    public void Compensation_rescues_a_behaviour_that_is_merely_good_at_everything()
    {
        // Three axes at 800 multiply to 512 — a behaviour good at everything scoring below one that
        // is mediocre at a single thing. Compensation is what keeps behaviours with different
        // numbers of axes comparable at all.
        var three = new[]
        {
            new Consideration("a", ResponseCurve.Linear, 800),
            new Consideration("b", ResponseCurve.Linear, 800),
            new Consideration("c", ResponseCurve.Linear, 800)
        };

        Assert.True(Considerations.Score(three) > 512);
    }

    [Fact]
    public void Compensation_never_carries_anything_past_the_ceiling()
    {
        for (var count = 1; count <= 8; count++)
            for (var score = 0; score <= 1000; score += 50)
                Assert.InRange(Considerations.Compensate(score, count), 0, 1000);
    }

    [Fact]
    public void A_single_consideration_is_left_exactly_as_it_scored()
    {
        // With nothing multiplied together there is nothing to compensate for, and inflating it
        // would make one-axis behaviours quietly beat every other kind.
        Assert.Equal(750, Considerations.Score(new[]
        {
            new Consideration("only", ResponseCurve.Linear, 750)
        }));
    }

    [Fact]
    public void Compensation_cannot_rescue_a_hopeless_behaviour()
    {
        Assert.Equal(0, Considerations.Compensate(0, 5));
        Assert.True(Considerations.Compensate(50, 5) < 200);
    }

    // ---- explaining itself ----------------------------------------------------------------------

    [Fact]
    public void The_weakest_axis_is_the_one_worth_naming()
    {
        var weakest = Considerations.Weakest(new[]
        {
            new Consideration("opportunity", ResponseCurve.Linear, 900),
            new Consideration("health", ResponseCurve.Linear, 40),
            new Consideration("distance", ResponseCurve.Linear, 600)
        });

        Assert.Equal("health", weakest!.Value.Name);
    }

    [Fact]
    public void A_tie_is_broken_the_same_way_every_time()
    {
        // Otherwise the same turn explains itself differently on two machines, and the audit trail
        // that exists to tell a mistake from a bug becomes a source of both.
        var tied = new[]
        {
            new Consideration("zeta", ResponseCurve.Linear, 100),
            new Consideration("alpha", ResponseCurve.Linear, 100)
        };

        Assert.Equal("alpha", Considerations.Weakest(tied)!.Value.Name);
        Assert.Equal("alpha", Considerations.Weakest(tied.Reverse().ToArray())!.Value.Name);
    }

    [Fact]
    public void Nothing_to_consider_has_no_weakest_link()
    {
        Assert.Null(Considerations.Weakest(Array.Empty<Consideration>()));
    }
}
