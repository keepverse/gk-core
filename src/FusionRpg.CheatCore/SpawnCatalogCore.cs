namespace FusionRpg.CheatCore;

public sealed class SpawnEntryDto
{
    public string Side { get; set; } = "";
    public int Type { get; set; }
    public string TypeName { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> Sources { get; set; } = new();
    public bool? SpawnOk { get; set; }
    public string? LastError { get; set; }
}

public sealed class SpawnCatalogCore
{
    readonly object _gate = new();
    readonly Dictionary<(string side, int type), SpawnEntryDto> _entries = new();

    public int Count(string side)
    {
        lock (_gate) return _entries.Keys.Count(k => k.side == side);
    }

    public void Note(string side, int type, string? typeName, string source, string? displayName = null)
    {
        if (type < 0) return;
        if (string.Equals(typeName, "Nothing", StringComparison.Ordinal)) return;
        var key = (side, type);
        lock (_gate)
        {
            if (!_entries.TryGetValue(key, out var e))
            {
                e = new SpawnEntryDto
                {
                    Side = side,
                    Type = type,
                    TypeName = typeName ?? type.ToString(),
                    DisplayName = displayName
                };
                _entries[key] = e;
            }
            if (!string.IsNullOrWhiteSpace(displayName)) e.DisplayName = displayName;
            if (!string.IsNullOrWhiteSpace(typeName)) e.TypeName = typeName!;
            if (!e.Sources.Contains(source)) e.Sources.Add(source);
        }
    }

    public List<SpawnEntryDto> List(string side)
    {
        lock (_gate)
            return _entries.Values.Where(e => e.Side == side)
                .OrderBy(e => e.Type)
                .Select(e => new SpawnEntryDto
                {
                    Side = e.Side,
                    Type = e.Type,
                    TypeName = e.TypeName,
                    DisplayName = e.DisplayName,
                    Sources = e.Sources.ToList(),
                    SpawnOk = e.SpawnOk,
                    LastError = e.LastError
                }).ToList();
    }

    public void MarkSpawn(string side, int type, bool ok, string? error = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue((side, type), out var e)) return;
            e.SpawnOk = ok;
            e.LastError = error;
        }
    }

    public void ClearFailed()
    {
        lock (_gate)
        {
            foreach (var e in _entries.Values.Where(x => x.SpawnOk == false))
            {
                e.SpawnOk = null;
                e.LastError = null;
            }
        }
    }
}
