namespace FusionRpg.Core.Combat;

public sealed class BoardEntitySnap
{
    public string Ptr { get; init; } = "";
    public string Side { get; init; } = "";
    public int TypeId { get; init; } = -1;
    public int Col { get; init; }
    public int Row { get; init; }
    public bool MindControlled { get; init; }
    public bool Living { get; init; } = true;
}

/// <summary>Frozen lawn census for one EffectBag flush. Immutable after construction.</summary>
public sealed class BoardSnapshot
{
    public IReadOnlyList<BoardEntitySnap> Entities { get; }

    public BoardSnapshot(IEnumerable<BoardEntitySnap>? entities = null)
    {
        Entities = (entities ?? Array.Empty<BoardEntitySnap>())
            .Where(e => e != null && !string.IsNullOrWhiteSpace(e.Ptr) && e.Living)
            .ToList();
    }

    public static BoardSnapshot Empty { get; } = new();

    public BoardEntitySnap? FindPtr(string? ptr)
    {
        var want = CombatPtr.Normalize(ptr);
        if (string.IsNullOrEmpty(want)) return null;
        return Entities.FirstOrDefault(e => CombatPtr.EqualsPtr(e.Ptr, want));
    }
}
