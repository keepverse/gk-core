namespace FusionRpg.Core.Match;

/// <summary>
/// Single-writer live match FSM (W1–W5). Replay / Injector wire are host concerns.
/// Never references FusionRpg.Data.
/// </summary>
public sealed class MatchRuntime
{
    public const int ContractVersion = 1;

    /// <summary>
    /// Protects Core / offline Replay. LIVE W2 Emit stays main-thread affinity
    /// (match-runtime §10); do not treat this lock as a cross-thread Emit license.
    /// </summary>
    readonly object _gate = new();
    readonly MatchState _state = new();
    UniqueBinding? _lastBound;

    /// <summary>
    /// buff-debuff-scope T5/T6: forwards Bound/Cleared (from `_state.UniqueBindings`, resubscribed
    /// every reset below since that facet is a whole new instance each match — see
    /// <see cref="ResetForNewMatch"/>) and MindControlToggled (raised directly from `Apply` below).
    /// One stable event surviving match resets, rather than a subscription that goes silently stale
    /// after the first one.
    /// </summary>
    public event Action<ScopeMembershipEvent>? MembershipChanged;

    public MatchRuntime()
    {
        _state.UniqueBindings.MembershipChanged += RaiseMembershipChanged;
    }

    void RaiseMembershipChanged(ScopeMembershipEvent e) => MembershipChanged?.Invoke(e);

    public MatchPhase Phase
    {
        get { lock (_gate) return _state.Phase; }
    }

    public string? MatchKey
    {
        get { lock (_gate) return _state.MatchKey; }
    }

    public long Revision
    {
        get { lock (_gate) return _state.Revision; }
    }

    public int UniqueBindingCount
    {
        get { lock (_gate) return _state.UniqueBindings.Count; }
    }

    /// <summary>Fold capture kind + payload. Unknown kinds are no-ops (except unique ack bind).</summary>
    public void Apply(string kind, IReadOnlyDictionary<string, object>? payload = null)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;
        lock (_gate)
        {
            if (string.Equals(kind, "board.start", StringComparison.OrdinalIgnoreCase))
            {
                EnterStarting(payload);
                return;
            }

            if (string.Equals(kind, "board.end", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "match.result", StringComparison.OrdinalIgnoreCase))
            {
                EnterEnding();
                return;
            }

            // place: Activity/runs only — never living membership
            if (string.Equals(kind, "plant.place", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(kind, "zombie.place", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (string.Equals(kind, "pvz.spawn.extra.ack", StringComparison.OrdinalIgnoreCase))
            {
                if (CanFoldBoard())
                    TryBindFromPayload(payload);
                return;
            }

            if (!CanFoldBoard()) return;

            if (string.Equals(kind, "plant.spawn", StringComparison.OrdinalIgnoreCase))
            {
                if (_state.Board.TryUpsert(BoardSide.Plant, ReadPtr(payload), ReadTypeId(payload)))
                    Bump();
                TryBindFromPayload(payload);
                return;
            }

            if (string.Equals(kind, "zombie.spawn", StringComparison.OrdinalIgnoreCase))
            {
                if (_state.Board.TryUpsert(BoardSide.Zombie, ReadPtr(payload), ReadTypeId(payload)))
                    Bump();
                TryBindFromPayload(payload);
                return;
            }

            if (string.Equals(kind, "plant.die", StringComparison.OrdinalIgnoreCase))
            {
                var ptr = ReadPtr(payload);
                if (_state.Board.TryRemove(BoardSide.Plant, ptr))
                    Bump();
                if (_state.UniqueBindings.TryClearByPtr(ptr))
                    Bump();
                return;
            }

            if (string.Equals(kind, "zombie.die", StringComparison.OrdinalIgnoreCase))
            {
                var ptr = ReadPtr(payload);
                if (_state.Board.TryRemove(BoardSide.Zombie, ptr))
                    Bump();
                if (_state.UniqueBindings.TryClearByPtr(ptr))
                    Bump();
                return;
            }

            if (string.Equals(kind, "zombie.hypno", StringComparison.OrdinalIgnoreCase))
            {
                var ptr = ReadPtr(payload);
                if (string.IsNullOrWhiteSpace(ptr)) return;
                var p = MatchUniqueBindingsFacet.NormalizePtr(ptr!);
                var mc = ReadBool(payload, "isMindControlled");

                // Idempotent: two events reporting the same state in a row (the real Harmony postfix
                // fires on every SetMindControl call, not only on an actual flip) must not double-Bump,
                // matching plant.die/zombie.die's own "if (...TryRemove...) Bump()" pattern of only
                // bumping on a real change.
                var changed = mc ? _state.MindControl.Add(p) : _state.MindControl.Remove(p);
                if (changed)
                    Bump();
                RaiseMembershipChanged(new ScopeMembershipEvent(p, ScopeMembershipTransition.MindControlToggled, mc));
                return;
            }

            // W1 later: bullet.init, …
        }
    }

    /// <summary>
    /// Unique deploy Intent accepted → PendingSpawn. No board mutate.
    /// Idempotent for same instanceId + correlationId while Pending.
    /// </summary>
    public bool TryBeginPending(
        string instanceId,
        string correlationId,
        string side,
        int typeId,
        string? loadoutJson = null)
    {
        lock (_gate)
        {
            if (_state.Phase is not (MatchPhase.InMatch or MatchPhase.Paused))
                return false;
            if (!_state.UniqueBindings.TryBeginPending(instanceId, correlationId, side, typeId, loadoutJson))
                return false;
            Bump();
            return true;
        }
    }

    /// <summary>Consume the most recent Pending→Bound transition (Injector loadout apply).</summary>
    public UniqueBinding? ConsumeLastBound()
    {
        lock (_gate)
        {
            var b = _lastBound;
            _lastBound = null;
            return b;
        }
    }

    /// <summary>
    /// FailDeploy / timeout / spawn-null: clear PendingSpawn (or Bound) by instance and/or correlation.
    /// Prefer correlationId when both provided.
    /// </summary>
    public bool TryClearPending(string? instanceId = null, string? correlationId = null)
    {
        lock (_gate)
        {
            var cleared = false;
            if (!string.IsNullOrWhiteSpace(correlationId))
                cleared = _state.UniqueBindings.TryClearByCorrelation(correlationId);
            if (!cleared && !string.IsNullOrWhiteSpace(instanceId))
                cleared = _state.UniqueBindings.TryClearByInstance(instanceId);
            if (cleared)
            {
                if (_lastBound != null &&
                    ((!string.IsNullOrWhiteSpace(instanceId) &&
                      string.Equals(_lastBound.InstanceId, instanceId.Trim(), StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrWhiteSpace(correlationId) &&
                      string.Equals(_lastBound.CorrelationId, correlationId.Trim(), StringComparison.OrdinalIgnoreCase))))
                    _lastBound = null;
                Bump();
            }
            return cleared;
        }
    }

    public bool TryGetBinding(string instanceId, out UniqueBinding? binding)
    {
        lock (_gate)
            return _state.UniqueBindings.TryGet(instanceId, out binding);
    }

    public bool TryGetBindingByPtr(string? ptr, out UniqueBinding? binding)
    {
        lock (_gate)
            return _state.UniqueBindings.TryGetByPtr(ptr, out binding);
    }

    /// <summary>
    /// Gate our Intent / FA4 / debug Create before Unity spawn. No board mutate, no revision bump.
    /// Counts come from BoardProjection; phase must be InMatch.
    /// </summary>
    public bool TryAdmitSpawn(string? side, out GateResult result)
    {
        lock (_gate)
        {
            if (_state.Phase != MatchPhase.InMatch)
            {
                result = GateResult.Reject(GateReasons.ForPhase(_state.Phase));
                return false;
            }

            var counts = new LivingCounts(
                _state.Board.PlantCount,
                _state.Board.ZombieCount,
                _state.Board.BulletCount);
            result = CapPolicy.TryAdmit(side, counts, _state.Caps);
            return result.Ok;
        }
    }

    /// <summary>Admit by BoardSide enum (invalid enum → cap.invalid_side).</summary>
    public bool TryAdmitSpawn(BoardSide side, out GateResult result)
    {
        var name = side switch
        {
            BoardSide.Plant => "plant",
            BoardSide.Zombie => "zombie",
            BoardSide.Bullet => "bullet",
            _ => null
        };
        if (name == null)
        {
            lock (_gate)
            {
                if (_state.Phase != MatchPhase.InMatch)
                {
                    result = GateResult.Reject(GateReasons.ForPhase(_state.Phase));
                    return false;
                }

                result = GateResult.Reject(GateReasons.CapInvalidSide);
                return false;
            }
        }

        return TryAdmitSpawn(name, out result);
    }

    /// <summary>
    /// Replace RAM CapPolicyConfig (Cheats / W2-E seam). No revision bump.
    /// Null config is ignored. Allowed in Idle, InMatch, or Paused.
    /// </summary>
    public void ConfigureCaps(CapPolicyConfig? config)
    {
        if (config == null) return;
        lock (_gate)
        {
            if (_state.Phase is not (MatchPhase.Idle or MatchPhase.InMatch or MatchPhase.Paused))
                return;
            _state.Caps = CopyCaps(config);
        }
    }

    public void NotifyPaused(bool paused)
    {
        lock (_gate)
        {
            if (paused)
            {
                if (_state.Phase != MatchPhase.InMatch) return;
                _state.Phase = MatchPhase.Paused;
                Bump();
                return;
            }

            if (_state.Phase != MatchPhase.Paused) return;
            _state.Phase = MatchPhase.InMatch;
            Bump();
        }
    }

    public MatchSnapshot ToSnapshot()
    {
        lock (_gate)
        {
            return new MatchSnapshot
            {
                ContractVersion = ContractVersion,
                Phase = _state.Phase,
                MatchKey = _state.MatchKey,
                Revision = _state.Revision,
                PlantCount = _state.Board.PlantCount,
                ZombieCount = _state.Board.ZombieCount,
                BulletCount = _state.Board.BulletCount,
                Entities = _state.Board.ToEntityArray(),
                DebugActive = _state.Debug.Active,
                ScenarioId = _state.Debug.ScenarioId,
                EffectSessionActive = _state.Effect.SessionActive,
                Caps = CopyCaps(_state.Caps),
                Bindings = _state.UniqueBindings.ToArray()
            };
        }
    }

    bool CanFoldBoard() =>
        _state.Phase is MatchPhase.InMatch or MatchPhase.Paused;

    void TryBindFromPayload(IReadOnlyDictionary<string, object>? payload)
    {
        var corr = ReadString(payload, "correlationId") ?? ReadString(payload, "CorrelationId");
        var instanceId = ReadString(payload, "instanceId") ?? ReadString(payload, "InstanceId");
        var ptr = ReadPtr(payload);
        if (string.IsNullOrWhiteSpace(ptr)) return;

        if (_state.UniqueBindings.TryBindOnSpawn(corr, instanceId, ptr!, out var bound) && bound != null)
        {
            _lastBound = bound;
            Bump();
        }
    }

    void EnterStarting(IReadOnlyDictionary<string, object>? payload)
    {
        // Fail-closed until match.restart / W2 restart policy — only Idle → Starting.
        if (_state.Phase is not MatchPhase.Idle) return;
        _state.Phase = MatchPhase.Starting;
        ResetForNewMatch(payload);
        _state.Phase = MatchPhase.InMatch;
        _state.Effect.SessionActive = true;
        Bump();
    }

    void EnterEnding()
    {
        if (_state.Phase is MatchPhase.Idle) return;
        _state.Phase = MatchPhase.Ending;
        ClearMatch();
        _state.Phase = MatchPhase.Idle;
        Bump();
    }

    void ResetForNewMatch(IReadOnlyDictionary<string, object>? payload)
    {
        ClearMatch();
        _state.MatchKey = ReadMatchKey(payload) ?? MintMatchKey();
        _state.Debug = new MatchDebugSessionFacet();
        _state.Effect = new MatchEffectSessionFacet();
        _state.UniqueBindings = new MatchUniqueBindingsFacet();
        _state.UniqueBindings.MembershipChanged += RaiseMembershipChanged;
        _lastBound = null;
    }

    void ClearMatch()
    {
        _state.MatchKey = null;
        _state.Effect.SessionActive = false;
        _state.Board.Clear();
        _state.Caps = CapPolicyConfig.Defaults();
        _state.Debug = new MatchDebugSessionFacet();
        _state.UniqueBindings.ClearAll();
        _state.UniqueBindings = new MatchUniqueBindingsFacet();
        _state.MindControl.Clear();
        _lastBound = null;
    }

    void Bump() => _state.Revision++;

    static string MintMatchKey() => "m-" + Guid.NewGuid().ToString("N")[..12];

    static string? ReadMatchKey(IReadOnlyDictionary<string, object>? payload)
    {
        if (payload == null) return null;
        if (!payload.TryGetValue("matchKey", out var v) || v == null) return null;
        var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    static string? ReadPtr(IReadOnlyDictionary<string, object>? payload)
    {
        if (payload == null) return null;
        if (TryGetPayload(payload, "ptr", out var v) || TryGetPayload(payload, "Ptr", out v))
        {
            var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        }

        return null;
    }

    static string? ReadString(IReadOnlyDictionary<string, object>? payload, string key)
    {
        if (payload == null) return null;
        if (!TryGetPayload(payload, key, out var v) || v == null) return null;
        var s = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    static bool ReadBool(IReadOnlyDictionary<string, object>? payload, string key)
    {
        if (payload == null) return false;
        if (!TryGetPayload(payload, key, out var v) || v == null) return false;
        try
        {
            return Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    static int ReadTypeId(IReadOnlyDictionary<string, object>? payload)
    {
        if (payload == null) return -1;
        if (TryGetPayload(payload, "typeId", out var v) || TryGetPayload(payload, "type", out v))
        {
            try
            {
                return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch
            {
                return -1;
            }
        }

        return -1;
    }

    static bool TryGetPayload(IReadOnlyDictionary<string, object> payload, string key, out object? value)
    {
        if (payload.TryGetValue(key, out value) && value != null) return true;
        value = null;
        return false;
    }

    static CapPolicyConfig CopyCaps(CapPolicyConfig src) => new()
    {
        MaxLivingPlants = src.MaxLivingPlants,
        MaxLivingZombies = src.MaxLivingZombies,
        MaxLivingBullets = src.MaxLivingBullets
    };
}
