namespace FusionRpg.Core.Match;

/// <summary>Ephemeral live bind phase for a durable UniqueActor specimen (W5-A).</summary>
public enum UniqueBindingPhase
{
    PendingSpawn = 0,
    Bound = 1,
    Cleared = 2
}

/// <summary>One ephemeral instanceId ↔ ptr row. Never durable SSOT.</summary>
public sealed class UniqueBinding
{
    public string InstanceId { get; set; } = "";
    public string? Ptr { get; set; }
    public string Side { get; set; } = "plant";
    public int TypeId { get; set; } = -1;
    public UniqueBindingPhase Phase { get; set; } = UniqueBindingPhase.PendingSpawn;
    public string? CorrelationId { get; set; }

    /// <summary>Optional minimal loadout JSON (deploy / mods stub). Empty = no-op.</summary>
    public string? LoadoutJson { get; set; }

    public UniqueBinding Clone() => new()
    {
        InstanceId = InstanceId,
        Ptr = Ptr,
        Side = Side,
        TypeId = TypeId,
        Phase = Phase,
        CorrelationId = CorrelationId,
        LoadoutJson = LoadoutJson
    };
}

/// <summary>RAM facet: PendingSpawn → Bound → Cleared. Cleared rows may be retained until match end.</summary>
public sealed class MatchUniqueBindingsFacet
{
    readonly Dictionary<string, UniqueBinding> _byInstance =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _corrToInstance =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, string> _ptrToInstance =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// buff-debuff-scope T5: raised alongside the existing Bound/Cleared transitions below — never
    /// changes when or whether they fire, only adds a signal for a scope's own-side WHO-value to
    /// react to (spec-membership-events.md).
    /// </summary>
    public event Action<ScopeMembershipEvent>? MembershipChanged;

    /// <summary>Count of PendingSpawn + Bound (excludes Cleared).</summary>
    public int Count
    {
        get
        {
            var n = 0;
            foreach (var b in _byInstance.Values)
            {
                if (b.Phase is UniqueBindingPhase.PendingSpawn or UniqueBindingPhase.Bound)
                    n++;
            }
            return n;
        }
    }

    public bool TryBeginPending(
        string instanceId,
        string correlationId,
        string side,
        int typeId,
        string? loadoutJson = null)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(correlationId))
            return false;

        var id = instanceId.Trim();
        var corr = correlationId.Trim();
        var s = NormalizeSide(side);

        if (_byInstance.TryGetValue(id, out var existing))
        {
            if (existing.Phase == UniqueBindingPhase.PendingSpawn &&
                string.Equals(existing.CorrelationId, corr, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(loadoutJson))
                    existing.LoadoutJson = loadoutJson;
                return true;
            }

            if (existing.Phase is UniqueBindingPhase.PendingSpawn or UniqueBindingPhase.Bound)
                return false;
        }

        if (_corrToInstance.ContainsKey(corr))
            return false;

        var row = new UniqueBinding
        {
            InstanceId = id,
            Ptr = null,
            Side = s,
            TypeId = typeId,
            Phase = UniqueBindingPhase.PendingSpawn,
            CorrelationId = corr,
            LoadoutJson = string.IsNullOrWhiteSpace(loadoutJson) ? null : loadoutJson.Trim()
        };
        _byInstance[id] = row;
        _corrToInstance[corr] = id;
        return true;
    }

    /// <summary>
    /// PendingSpawn → Bound when spawn/ack carries matching correlationId (or instanceId).
    /// </summary>
    public bool TryBindOnSpawn(string? correlationId, string? instanceId, string ptr, out UniqueBinding? bound)
    {
        bound = null;
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        var p = NormalizePtr(ptr);

        UniqueBinding? row = null;
        if (!string.IsNullOrWhiteSpace(correlationId) &&
            _corrToInstance.TryGetValue(correlationId.Trim(), out var byCorr) &&
            _byInstance.TryGetValue(byCorr, out row))
        {
            /* matched by corr */
        }
        else if (!string.IsNullOrWhiteSpace(instanceId) &&
                 _byInstance.TryGetValue(instanceId.Trim(), out row))
        {
            /* matched by instance */
        }

        if (row is null || row.Phase != UniqueBindingPhase.PendingSpawn)
            return false;

        if (!string.IsNullOrWhiteSpace(row.Ptr))
            _ptrToInstance.Remove(NormalizePtr(row.Ptr));

        row.Ptr = p;
        row.Phase = UniqueBindingPhase.Bound;
        _ptrToInstance[p] = row.InstanceId;
        bound = row.Clone();
        MembershipChanged?.Invoke(new ScopeMembershipEvent(p, ScopeMembershipTransition.Bound));
        return true;
    }

    public bool TryClearByPtr(string? ptr)
    {
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        var p = NormalizePtr(ptr);
        if (!_ptrToInstance.TryGetValue(p, out var id)) return false;
        return ClearInstance(id);
    }

    public bool TryClearByInstance(string? instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        return ClearInstance(instanceId.Trim());
    }

    public bool TryClearByCorrelation(string? correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) return false;
        if (!_corrToInstance.TryGetValue(correlationId.Trim(), out var id)) return false;
        return ClearInstance(id);
    }

    public void ClearAll()
    {
        foreach (var b in _byInstance.Values)
        {
            b.Phase = UniqueBindingPhase.Cleared;
            b.Ptr = null;
        }
        _ptrToInstance.Clear();
        _corrToInstance.Clear();
        _byInstance.Clear();
    }

    public bool TryGet(string instanceId, out UniqueBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(instanceId)) return false;
        if (!_byInstance.TryGetValue(instanceId.Trim(), out var row)) return false;
        binding = row.Clone();
        return true;
    }

    public bool TryGetByPtr(string? ptr, out UniqueBinding? binding)
    {
        binding = null;
        if (string.IsNullOrWhiteSpace(ptr)) return false;
        if (!_ptrToInstance.TryGetValue(NormalizePtr(ptr), out var id)) return false;
        return TryGet(id, out binding);
    }

    public UniqueBinding[] ToArray()
    {
        if (_byInstance.Count == 0) return Array.Empty<UniqueBinding>();
        var arr = new UniqueBinding[_byInstance.Count];
        var i = 0;
        foreach (var b in _byInstance.Values)
            arr[i++] = b.Clone();
        return arr;
    }

    bool ClearInstance(string id)
    {
        if (!_byInstance.TryGetValue(id, out var row)) return false;
        if (row.Phase == UniqueBindingPhase.Cleared) return false;

        var clearedPtr = string.IsNullOrWhiteSpace(row.Ptr) ? null : NormalizePtr(row.Ptr);
        if (clearedPtr is not null)
            _ptrToInstance.Remove(clearedPtr);
        if (!string.IsNullOrWhiteSpace(row.CorrelationId))
            _corrToInstance.Remove(row.CorrelationId);

        row.Phase = UniqueBindingPhase.Cleared;
        row.Ptr = null;
        if (clearedPtr is not null)
            MembershipChanged?.Invoke(new ScopeMembershipEvent(clearedPtr, ScopeMembershipTransition.Cleared));
        return true;
    }

    static string NormalizeSide(string? side)
    {
        var s = (side ?? "plant").Trim().ToLowerInvariant();
        return s is "plant" or "zombie" ? s : "plant";
    }

    public static string NormalizePtr(string ptr)
    {
        var p = ptr.Trim();
        if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            p = p[2..];
        return p.ToUpperInvariant();
    }
}
