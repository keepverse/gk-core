namespace FusionRpg.Core.Combat;

/// <summary>
/// Fallback shooter identity for a bullet whose engine <c>from</c>/<c>from_zombie</c> field is unset at
/// spawn (lawn-combat-wire fifth defect). A bullet is created at its shooter's cell, so the shooter is
/// the same-side living entity in the bullet's row nearest the bullet's spawn column. A row holds many
/// same-side entities, so row alone is never an identity.
///
/// <para>Rules, in order: no column (&lt; 0) → no match; candidates must be living, same row, same
/// object-kind side, and at most <see cref="MaxColumnDistance"/> columns away; nearest wins; at equal
/// distance the occupant on the shooter side of the pea wins (plant: column ≤ bullet, zombie: column ≥
/// bullet); any remaining tie → no match. No match means no RPG record — never a guess.</para>
///
/// <para>Known residual: a multi-lane shot (Threepeater side pea) is stamped with the neighbouring row, so
/// an occupant of that row at the shooter's column is matched. Pinned by a test so it stays visible.</para>
/// </summary>
public static class BulletShooterMatch
{
    /// <summary>Structural: a pea spawns inside or at the edge of its shooter's cell, never further.</summary>
    public const int MaxColumnDistance = 1;

    public static BoardEntitySnap? Resolve(IReadOnlyList<BoardEntitySnap> entities, string side, int row, int bulletCol)
    {
        if (entities is null || bulletCol < 0) return null;
        var plantSide = string.Equals(side, "plant", StringComparison.OrdinalIgnoreCase);

        BoardEntitySnap? best = null;
        var bestScore = int.MaxValue;
        var tie = false;
        foreach (var e in entities)
        {
            if (e is null || !e.Living || e.Row != row || !string.Equals(e.Side, side, StringComparison.OrdinalIgnoreCase))
                continue;
            var dist = Math.Abs(e.Col - bulletCol);
            if (dist > MaxColumnDistance) continue;
            var behind = plantSide ? e.Col <= bulletCol : e.Col >= bulletCol;
            var score = dist * 2 + (behind ? 0 : 1);
            if (score < bestScore) { best = e; bestScore = score; tie = false; }
            else if (score == bestScore) tie = true;
        }
        return tie ? null : best;
    }
}
