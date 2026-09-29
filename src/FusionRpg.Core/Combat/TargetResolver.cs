using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Lawn;

namespace FusionRpg.Core.Combat;

/// <summary>Pure target selection. Core-only — no Unity. Selected mode returns empty (debug injects ptr).</summary>
public static class TargetResolver
{
    public static IReadOnlyList<string> Resolve(
        TargetSpec spec,
        BoardSnapshot snapshot,
        EffectEventDto? ev,
        CombatPolicy? policy = null,
        ICombatRng? rng = null)
    {
        policy ??= CombatPolicy.Default;
        spec ??= new TargetSpec();
        snapshot ??= BoardSnapshot.Empty;
        var mode = spec.Mode ?? TargetModes.EventTarget;

        if (string.Equals(mode, TargetModes.Selected, StringComparison.OrdinalIgnoreCase))
            return Array.Empty<string>();

        if (string.Equals(mode, TargetModes.EventTarget, StringComparison.OrdinalIgnoreCase))
            return SingleOrEmpty(ev?.TargetPtr);

        if (string.Equals(mode, TargetModes.Actor, StringComparison.OrdinalIgnoreCase))
            return SingleOrEmpty(ev?.ActorPtr);

        if (string.Equals(mode, TargetModes.Single, StringComparison.OrdinalIgnoreCase))
            return SingleOrEmpty(spec.Ptr);

        var pool = FilterPool(snapshot.Entities, spec.Filters).ToList();
        pool.Sort((a, b) => string.Compare(a.Ptr, b.Ptr, StringComparison.OrdinalIgnoreCase));

        if (string.Equals(mode, TargetModes.All, StringComparison.OrdinalIgnoreCase))
            return Cap(pool.Select(e => e.Ptr), policy.ResolveMaxTargets(spec.MaxTargets));

        if (string.Equals(mode, TargetModes.Multi, StringComparison.OrdinalIgnoreCase))
        {
            var count = spec.Count is > 0 ? spec.Count.Value : 1;
            count = Math.Min(count, policy.ResolveMaxTargets(spec.MaxTargets));
            return Cap(pool.Select(e => e.Ptr), count);
        }

        if (string.Equals(mode, TargetModes.Random, StringComparison.OrdinalIgnoreCase))
        {
            var count = spec.Count is > 0 ? spec.Count.Value : 1;
            count = Math.Min(count, policy.ResolveMaxTargets(spec.MaxTargets));
            return PickRandom(pool, count, rng ?? new SeededCombatRng(0));
        }

        if (string.Equals(mode, TargetModes.Area, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryAnchor(spec, ev, snapshot, out var col, out var row))
                return Array.Empty<string>();
            var cells = EnumerateCells(spec, col, row, policy);
            var inArea = pool.Where(e => cells.Contains((e.Col, e.Row))).ToList();
            return Cap(inArea.Select(e => e.Ptr), policy.ResolveMaxTargets(spec.MaxTargets));
        }

        return Array.Empty<string>();
    }

    static IReadOnlyList<string> SingleOrEmpty(string? ptr) =>
        string.IsNullOrWhiteSpace(ptr) ? Array.Empty<string>() : new[] { ptr.Trim() };

    static IReadOnlyList<string> Cap(IEnumerable<string> ptrs, int max) =>
        ptrs.Take(Math.Max(0, max)).ToList();

    static IReadOnlyList<string> PickRandom(List<BoardEntitySnap> pool, int count, ICombatRng rng)
    {
        if (pool.Count == 0 || count <= 0) return Array.Empty<string>();
        var copy = pool.ToList();
        for (var i = copy.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }

        return copy.Take(Math.Min(count, copy.Count)).Select(e => e.Ptr).ToList();
    }

    static IEnumerable<BoardEntitySnap> FilterPool(
        IReadOnlyList<BoardEntitySnap> entities,
        Dictionary<string, object?>? filters)
    {
        if (filters == null || filters.Count == 0)
            return entities.Where(e => !e.MindControlled);
        var map = JsonOverlay.FromObject(filters);
        var side = JsonOverlay.GetString(map, "side");
        int? typeId = map.ContainsKey("typeId") ? JsonOverlay.GetInt(map, "typeId", int.MinValue) : null;
        if (typeId == int.MinValue) typeId = null;
        var typeIdIn = ParseIntList(map, "typeIdIn");
        var excludeMc = map.ContainsKey("excludeMindControlled")
            ? JsonOverlay.GetBool(map, "excludeMindControlled")
            : string.IsNullOrEmpty(side) ||
              string.Equals(side, "zombie", StringComparison.OrdinalIgnoreCase);
        int? row = map.ContainsKey("row") ? JsonOverlay.GetInt(map, "row") : null;
        int? colMin = null, colMax = null, colExact = null;
        if (map.TryGetValue("col", out var colVal) && colVal != null)
        {
            if (colVal is Dictionary<string, object?> colObj)
            {
                if (colObj.ContainsKey("min")) colMin = JsonOverlay.GetInt(colObj, "min");
                if (colObj.ContainsKey("max")) colMax = JsonOverlay.GetInt(colObj, "max");
            }
            else
                colExact = Convert.ToInt32(colVal, System.Globalization.CultureInfo.InvariantCulture);
        }

        return entities.Where(e =>
        {
            if (!string.IsNullOrEmpty(side) &&
                !string.Equals(e.Side, side, StringComparison.OrdinalIgnoreCase))
                return false;
            if (typeId.HasValue && e.TypeId != typeId.Value) return false;
            if (typeIdIn != null && typeIdIn.Count > 0 && !typeIdIn.Contains(e.TypeId)) return false;
            if (excludeMc && e.MindControlled) return false;
            if (row.HasValue && e.Row != row.Value) return false;
            if (colExact.HasValue && e.Col != colExact.Value) return false;
            if (colMin.HasValue && e.Col < colMin.Value) return false;
            if (colMax.HasValue && e.Col > colMax.Value) return false;
            return true;
        });
    }

    static List<int>? ParseIntList(Dictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var v) || v == null) return null;
        if (v is IEnumerable<object?> seq)
            return seq.Select(x => Convert.ToInt32(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        return null;
    }

    static bool TryAnchor(TargetSpec spec, EffectEventDto? ev, BoardSnapshot snapshot, out int col, out int row)
    {
        col = 0;
        row = 0;
        if (spec.Anchor != null && spec.Anchor is not string)
        {
            var dict = JsonOverlay.FromObject(spec.Anchor);
            if (!dict.ContainsKey("col") || !dict.ContainsKey("row"))
                return false;
            col = JsonOverlay.GetInt(dict, "col");
            row = JsonOverlay.GetInt(dict, "row");
            return true;
        }

        if (spec.Anchor is string s &&
            !string.Equals(s, "EventTarget", StringComparison.OrdinalIgnoreCase))
            return false;

        var ptr = ev?.TargetPtr;
        var found = snapshot.FindPtr(ptr);
        if (found == null) return false;
        col = found.Col;
        row = found.Row;
        return true;
    }

    static HashSet<(int Col, int Row)> EnumerateCells(TargetSpec spec, int col, int row, CombatPolicy policy)
    {
        var lastCol = policy.LastCol;
        var lastRow = policy.LastRow;
        col = LawnCoordMath.ClampIndex(col, lastCol);
        row = LawnCoordMath.ClampIndex(row, lastRow);
        var cells = new HashSet<(int, int)>();
        if (string.IsNullOrWhiteSpace(spec.Shape))
            return cells;
        var shape = spec.Shape;

        if (string.Equals(shape, AreaShapes.Row, StringComparison.OrdinalIgnoreCase))
        {
            for (var c = 0; c <= lastCol; c++) cells.Add((c, row));
            return cells;
        }

        if (string.Equals(shape, AreaShapes.Column, StringComparison.OrdinalIgnoreCase))
        {
            for (var r = 0; r <= lastRow; r++) cells.Add((col, r));
            return cells;
        }

        if (string.Equals(shape, AreaShapes.Square, StringComparison.OrdinalIgnoreCase))
        {
            var n = spec.Size is > 0 ? spec.Size.Value : policy.AreaDefaultSquareSize;
            var half = Math.Max(0, (n - 1) / 2);
            AddRect(cells, col - half, row - half, n, n, lastCol, lastRow);
            return cells;
        }

        if (string.Equals(shape, AreaShapes.Rectangle, StringComparison.OrdinalIgnoreCase))
        {
            var w = spec.Width is > 0 ? spec.Width.Value : policy.AreaDefaultRectangleWidth;
            var h = spec.Height is > 0 ? spec.Height.Value : policy.AreaDefaultRectangleHeight;
            var origin = spec.AnchorOrigin ?? AnchorOrigins.Center;
            int x0, y0;
            if (string.Equals(origin, AnchorOrigins.Corner, StringComparison.OrdinalIgnoreCase))
            {
                x0 = col;
                y0 = row;
            }
            else
            {
                x0 = col - Math.Max(0, (w - 1) / 2);
                y0 = row - Math.Max(0, (h - 1) / 2);
            }

            AddRect(cells, x0, y0, w, h, lastCol, lastRow);
        }

        return cells;
    }

    static void AddRect(HashSet<(int, int)> cells, int x0, int y0, int w, int h, int lastCol, int lastRow)
    {
        for (var c = x0; c < x0 + w; c++)
        for (var r = y0; r < y0 + h; r++)
        {
            if (c < 0 || r < 0 || c > lastCol || r > lastRow) continue;
            cells.Add((c, r));
        }
    }
}
