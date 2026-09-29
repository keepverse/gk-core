using FusionRpg.Core.Items;

namespace FusionRpg.Core.Delve.Pack;

/// <summary>
/// The two already-resolved lookups <see cref="Footprint.Derive"/> needs: a role's own base cell count
/// on <see cref="Footprint.ShapeLadder"/>, and a mass-class's own step along it (spec-loot-pack.md §2).
///
/// <para>This is a plain data holder, not a loader: D3.19's own <c>PackFootprintTable.Build</c> is what
/// reads `pack.footprint.role.*`/`pack.footprint.massStep.*` out of `dungeon.v1.json` and constructs one
/// of these — "refuse at load, naming the key" (§2, §9) is that loader's own job, not this record's.
/// This record's own lookups throw plainly on an unknown key so a caller can never silently read a
/// wrong footprint, but the FRIENDLY, key-naming refusal belongs to the loader that already has the raw
/// key string on hand.</para>
/// </summary>
public sealed record PackTuning(
    IReadOnlyDictionary<ItemRole, int> RoleCells, IReadOnlyDictionary<string, int> MassSteps)
{
    public int FootprintRoleCells(ItemRole role) =>
        RoleCells.TryGetValue(role, out var cells)
            ? cells
            : throw new KeyNotFoundException($"pack.footprint.role: no entry for '{ItemRoles.Id(role)}'");

    public int MassStep(string massClass) =>
        MassSteps.TryGetValue(massClass, out var step)
            ? step
            : throw new KeyNotFoundException($"pack.footprint.massStep: no entry for '{massClass}'");
}

/// <summary>
/// spec-loot-pack.md §2 — footprint(role, massClass) is DERIVED, never authored: a role's own base
/// cell count on <see cref="ShapeLadder"/>, stepped by the item's mass class, clamped into the ladder,
/// then oriented tall (Weapon ladder) or broad (everything else). Pure — no store, no RNG, no clock.
/// </summary>
public static class Footprint
{
    // STRUCTURAL: the closed list of legal rectangles as cell counts -- not a balance number; a
    // seventh shape is a code change with a comment, never a tuning edit. massStep steps along this index.
    public static readonly int[] ShapeLadder = { 1, 2, 3, 4, 6, 8 };

    static readonly IReadOnlyDictionary<int, (int W, int H)> Tall = new Dictionary<int, (int, int)>
    {
        [1] = (1, 1), [2] = (1, 2), [3] = (1, 3), [4] = (1, 4), [6] = (2, 3), [8] = (2, 4),
    };

    static readonly IReadOnlyDictionary<int, (int W, int H)> Broad = new Dictionary<int, (int, int)>
    {
        [1] = (1, 1), [2] = (2, 1), [3] = (3, 1), [4] = (2, 2), [6] = (3, 2), [8] = (4, 2),
    };

    /// <summary>The ladder index for a role's own base cell count, stepped by mass class and clamped
    /// into the ladder's own bounds — "the clamp bounds an index into a six-entry list — structural by
    /// nature... not a magnitude clamp" (§2).</summary>
    public static int CellsFor(int roleCells, int massStep)
    {
        var baseIndex = Array.IndexOf(ShapeLadder, roleCells);
        if (baseIndex < 0)
            throw new ArgumentException($"pack.footprint: {roleCells} is not a ShapeLadder member", nameof(roleCells));
        var i = Math.Clamp(baseIndex + massStep, 0, ShapeLadder.Length - 1);
        return ShapeLadder[i];
    }

    /// <summary>Orients a cell count into a rectangle — tall for the Weapon ladder (§2's own six-shape
    /// tall table), broad for every other ladder (Armour, Offhand, Jewel, Standard). Pure.</summary>
    public static (int W, int H) Orient(int cells, ClassLadder ladder)
    {
        var table = ladder == ClassLadder.Weapon ? Tall : Broad;
        return table.TryGetValue(cells, out var wh)
            ? wh
            : throw new ArgumentException($"pack.footprint: {cells} is not a ShapeLadder member", nameof(cells));
    }

    /// <summary>footprint(role, massClass) — a base-type fact derived at load, never authored, never
    /// rolled, never on a rarity axis (§2). Orientation reuses the shipped role → class-ladder table
    /// (<see cref="BaseTypeSlate.LadderOf"/>) rather than a private copy.</summary>
    public static (int W, int H) Derive(ItemRole role, string massClass, PackTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        var cells = CellsFor(tuning.FootprintRoleCells(role), tuning.MassStep(massClass));
        var ladder = BaseTypeSlate.LadderOf(ItemRoles.Id(role));
        return Orient(cells, ladder);
    }
}
