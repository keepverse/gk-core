using FusionRpg.Core.Delve.Encounter;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Encounter;

/// <summary>D2.7 (spec-encounter-generator.md §8 "Closed-loop metric — cell coverage") —
/// `EncounterCoverage`: distinct `(postureMultiset, elementSpread, formation)` shapes, never raw
/// entries per cell.</summary>
public class EncounterCoverageTests
{
    static EncounterCell Cell(Formation formation, ElementTypeId[] spread, params Posture[] postures) =>
        new(postures, spread.ToHashSet(), formation);

    [Fact]
    public void An_empty_sequence_has_zero_distinct_cells()
    {
        Assert.Equal(0, EncounterCoverage.DistinctCells(Array.Empty<EncounterCell>()));
    }

    [Fact]
    public void The_identical_cell_seen_many_times_counts_once()
    {
        var cell = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Bastion, Posture.Force);
        var cells = Enumerable.Repeat(cell, 32);
        Assert.Equal(1, EncounterCoverage.DistinctCells(cells));
    }

    [Fact]
    public void Different_formations_are_different_cells()
    {
        var cells = new[]
        {
            Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force),
            Cell(Formation.Party, new[] { ElementTypeId.Fire }, Posture.Force),
        };
        Assert.Equal(2, EncounterCoverage.DistinctCells(cells));
    }

    [Fact]
    public void Different_element_spreads_are_different_cells()
    {
        var cells = new[]
        {
            Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force),
            Cell(Formation.Pack, new[] { ElementTypeId.Ice }, Posture.Force),
        };
        Assert.Equal(2, EncounterCoverage.DistinctCells(cells));
    }

    [Fact]
    public void The_posture_multiset_is_order_independent_but_multiplicity_sensitive()
    {
        var a = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Bastion, Posture.Force, Posture.Force);
        var b = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force, Posture.Bastion, Posture.Force); // same multiset, different order
        var c = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Bastion, Posture.Force); // different multiplicity (2 Force vs 1)

        Assert.Equal(1, EncounterCoverage.DistinctCells(new[] { a, b })); // a == b
        Assert.Equal(2, EncounterCoverage.DistinctCells(new[] { a, c })); // a != c
    }

    [Fact]
    public void An_element_spread_set_is_order_independent()
    {
        var a = Cell(Formation.Pack, new[] { ElementTypeId.Fire, ElementTypeId.Ice }, Posture.Force);
        var b = Cell(Formation.Pack, new[] { ElementTypeId.Ice, ElementTypeId.Fire }, Posture.Force);
        Assert.Equal(1, EncounterCoverage.DistinctCells(new[] { a, b }));
    }

    [Fact]
    public void DistinctCells_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterCoverage.DistinctCells(null!));
    }

    // ---- MeetsBudget, against the real budget.v1.json row ----

    [Theory]
    [InlineData(81, 81, 0, true)]   // exact target
    [InlineData(80, 81, 0, false)]  // one under, zero tolerance -- fails
    [InlineData(82, 81, 0, true)]   // over is always fine
    [InlineData(80, 81, 1, true)]   // one under, tolerance 1 -- passes
    [InlineData(40, 40, 0, true)]   // the real firstShip target, exactly met
    public void MeetsBudget_matches_the_real_dungeon_encounter_rows_own_tolerance_shape(int distinct, int target, int tolerance, bool expected)
    {
        Assert.Equal(expected, EncounterCoverage.MeetsBudget(distinct, target, tolerance));
    }

    // ---- SiblingCollisions ----

    [Fact]
    public void Two_siblings_on_the_same_row_landing_on_the_identical_cell_are_flagged()
    {
        var cell = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force);
        var result = EncounterCoverage.SiblingCollisions(new[] { ("row:0", cell), ("row:0", cell) });
        Assert.Equal(new[] { "row:0" }, result);
    }

    [Fact]
    public void Siblings_on_different_cells_are_not_flagged()
    {
        var a = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force);
        var b = Cell(Formation.Party, new[] { ElementTypeId.Fire }, Posture.Force);
        var result = EncounterCoverage.SiblingCollisions(new[] { ("row:0", a), ("row:0", b) });
        Assert.Empty(result);
    }

    [Fact]
    public void The_same_cell_across_DIFFERENT_sibling_groups_is_not_a_collision()
    {
        var cell = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force);
        var result = EncounterCoverage.SiblingCollisions(new[] { ("row:0", cell), ("row:1", cell) });
        Assert.Empty(result);
    }

    [Fact]
    public void A_group_is_reported_at_most_once_even_with_three_or_more_colliding_siblings()
    {
        var cell = Cell(Formation.Pack, new[] { ElementTypeId.Fire }, Posture.Force);
        var result = EncounterCoverage.SiblingCollisions(new[] { ("row:0", cell), ("row:0", cell), ("row:0", cell) });
        Assert.Equal(new[] { "row:0" }, result);
    }

    [Fact]
    public void SiblingCollisions_null_argument_throws()
    {
        Assert.Throws<ArgumentNullException>(() => EncounterCoverage.SiblingCollisions(null!));
    }
}
