using FusionRpg.Core.Actions;
using Xunit;

namespace FusionRpg.Core.Tests.Actions;

/// <summary>T7 (action-todo.md, spec-targeting.md §4, §6c). Chebyshev distance, the no-board
/// pass-through, and the Square-shape equivalence the whole targeting gate rests on.</summary>
public class GridDistanceTests
{
    [Theory]
    [InlineData(0, 0, 0, 0, 0)]
    [InlineData(0, 0, 3, 0, 3)]
    [InlineData(0, 0, 0, 3, 3)]
    [InlineData(0, 0, 3, 3, 3)]   // diagonal: Chebyshev, not Manhattan (which would say 6)
    [InlineData(0, 0, 2, 5, 5)]
    public void Chebyshev_is_max_of_the_two_axis_deltas(int r1, int c1, int r2, int c2, int expected)
    {
        Assert.Equal(expected, GridDistance.Chebyshev(new GridPos(r1, c1), new GridPos(r2, c2)));
    }

    [Fact]
    public void With_no_board_every_range_check_passes()
    {
        // Not an error, not empty -- spec-targeting.md §4's single most important line.
        Assert.True(GridDistance.InRange(null, new GridPos(0, 0), 1, 3));
        Assert.True(GridDistance.InRange(new GridPos(0, 0), null, 1, 3));
        Assert.True(GridDistance.InRange(null, null, 5, 5));
    }

    [Fact]
    public void With_a_board_range_excludes_outside_the_window()
    {
        var caster = new GridPos(0, 0);
        Assert.True(GridDistance.InRange(caster, new GridPos(0, 3), 1, 3));
        Assert.False(GridDistance.InRange(caster, new GridPos(0, 4), 1, 3));
        Assert.False(GridDistance.InRange(caster, new GridPos(0, 0), 1, 3)); // below MinRange
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void Square_contains_exactly_the_Chebyshev_ball_of_radius_n_minus_1_over_2(int size)
    {
        var center = new GridPos(10, 10);
        var radius = (size - 1) / 2;
        var square = GridDistance.Square(center, size);

        Assert.Equal(size * size, square.Count);
        Assert.All(square, cell => Assert.True(GridDistance.Chebyshev(center, cell) <= radius));

        // Every cell within the radius must actually be present -- not merely a subset.
        for (var dr = -radius; dr <= radius; dr++)
            for (var dc = -radius; dc <= radius; dc++)
                Assert.Contains(new GridPos(center.Row + dr, center.Col + dc), square);
    }
}
