using FusionRpg.Core.Combat;
using Xunit;

namespace FusionRpg.Core.Tests.Combat;

public class BulletShooterMatchTests
{
    static BoardEntitySnap E(string ptr, string side, int col, int row, bool living = true) =>
        new() { Ptr = ptr, Side = side, Col = col, Row = row, Living = living, TypeId = 0 };

    [Fact]
    public void Picks_the_shooter_not_the_first_plant_in_the_row()
    {
        var board = new[] { E("SUN", "plant", 0, 2), E("PEA", "plant", 3, 2), E("NUT", "plant", 6, 2) };
        Assert.Equal("PEA", BulletShooterMatch.Resolve(board, "plant", 2, 3)!.Ptr);
    }

    [Fact]
    public void Prefers_the_plant_behind_the_pea_at_equal_distance()
    {
        var board = new[] { E("PEA", "plant", 3, 2), E("NUT", "plant", 5, 2) };
        Assert.Equal("PEA", BulletShooterMatch.Resolve(board, "plant", 2, 4)!.Ptr);
    }

    [Fact]
    public void Prefers_the_zombie_on_the_far_side_of_the_pea_at_equal_distance()
    {
        var board = new[] { E("Z-NEAR-HOUSE", "zombie", 3, 1), E("Z-SHOOTER", "zombie", 5, 1) };
        Assert.Equal("Z-SHOOTER", BulletShooterMatch.Resolve(board, "zombie", 1, 4)!.Ptr);
    }

    [Fact]
    public void Two_candidates_with_the_same_score_drop_the_hit()
    {
        var board = new[] { E("A", "plant", 3, 2), E("B", "plant", 3, 2) };
        Assert.Null(BulletShooterMatch.Resolve(board, "plant", 2, 3));
    }

    [Fact]
    public void Unknown_column_drops_the_hit()
    {
        Assert.Null(BulletShooterMatch.Resolve(new[] { E("PEA", "plant", 3, 2) }, "plant", 2, -1));
    }

    [Fact]
    public void Nothing_within_one_column_drops_the_hit()
    {
        Assert.Null(BulletShooterMatch.Resolve(new[] { E("PEA", "plant", 0, 2) }, "plant", 2, 4));
    }

    [Fact]
    public void Ignores_dead_other_row_and_other_side_entities()
    {
        var board = new[]
        {
            E("DEAD", "plant", 3, 2, living: false),
            E("OTHER-ROW", "plant", 3, 1),
            E("ZOMBIE", "zombie", 3, 2),
            E("PEA", "plant", 2, 2)
        };
        Assert.Equal("PEA", BulletShooterMatch.Resolve(board, "plant", 2, 3)!.Ptr);
    }

    [Fact]
    public void Known_residual_a_side_lane_pea_matches_the_neighbouring_row_occupant()
    {
        // Threepeater at (col 2, row 2) fires a side pea stamped row 1; a Wall-nut sits at (col 2, row 1).
        var board = new[] { E("THREEPEATER", "plant", 2, 2), E("NUT", "plant", 2, 1) };
        Assert.Equal("NUT", BulletShooterMatch.Resolve(board, "plant", 1, 2)!.Ptr);
    }
}
