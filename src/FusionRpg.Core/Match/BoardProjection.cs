namespace FusionRpg.Core.Match;

public enum BoardSide
{
    Plant = 0,
    Zombie = 1,
    Bullet = 2
}

[Flags]
public enum BoardEntityFlags
{
    None = 0,
    Hypnotized = 1
}

/// <summary>Living overlay entity identity (spec BoardEntity).</summary>
public sealed class BoardEntity
{
    public string Ptr { get; set; } = "";
    public BoardSide Side { get; set; }
    public int TypeId { get; set; } = -1;
    public BoardEntityFlags Flags { get; set; }
}

/// <summary>
/// RAM living set for plants/zombies (W1-B). Membership = spawn/die only; place ignored.
/// Maps are private — use counts + ToEntityArray (avoids mutable entity ref leaks).
/// Bullets bucket reserved for a later slice.
/// </summary>
public sealed class BoardProjection
{
    static readonly StringComparer PtrComparer = StringComparer.OrdinalIgnoreCase;

    readonly Dictionary<string, BoardEntity> _plants = new(PtrComparer);
    readonly Dictionary<string, BoardEntity> _zombies = new(PtrComparer);
    readonly Dictionary<string, BoardEntity> _bullets = new(PtrComparer);

    public int PlantCount => _plants.Count;
    public int ZombieCount => _zombies.Count;
    public int BulletCount => _bullets.Count;

    /// <summary>
    /// Insert or update living entity. Returns false if ptr blank or existing row
    /// already has the same TypeId (idempotent — no revision bump upstream).
    /// </summary>
    public bool TryUpsert(BoardSide side, string? ptr, int typeId = -1)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        var key = ptr.Trim();
        var map = MapFor(side);
        if (map.TryGetValue(key, out var existing))
        {
            if (existing.TypeId == typeId) return false;
            existing.TypeId = typeId;
            existing.Side = side;
            return true;
        }

        map[key] = new BoardEntity
        {
            Ptr = key,
            Side = side,
            TypeId = typeId,
            Flags = BoardEntityFlags.None
        };
        return true;
    }

    public bool TryRemove(BoardSide side, string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        return MapFor(side).Remove(ptr.Trim());
    }

    public void Clear()
    {
        _plants.Clear();
        _zombies.Clear();
        _bullets.Clear();
    }

    /// <summary>Cold copy of all living entities (plants, then zombies, then bullets).</summary>
    public BoardEntity[] ToEntityArray()
    {
        var n = _plants.Count + _zombies.Count + _bullets.Count;
        if (n == 0) return Array.Empty<BoardEntity>();
        var arr = new BoardEntity[n];
        var i = 0;
        foreach (var e in _plants.Values) arr[i++] = CopyEntity(e);
        foreach (var e in _zombies.Values) arr[i++] = CopyEntity(e);
        foreach (var e in _bullets.Values) arr[i++] = CopyEntity(e);
        return arr;
    }

    Dictionary<string, BoardEntity> MapFor(BoardSide side) => side switch
    {
        BoardSide.Plant => _plants,
        BoardSide.Zombie => _zombies,
        BoardSide.Bullet => _bullets,
        _ => _plants
    };

    static BoardEntity CopyEntity(BoardEntity e) => new()
    {
        Ptr = e.Ptr,
        Side = e.Side,
        TypeId = e.TypeId,
        Flags = e.Flags
    };
}
