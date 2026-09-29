using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Stats;

namespace FusionRpg.Core.Effects;

/// <summary>
/// Secondary → Foundation command buffer. Sole plugin Grant path.
/// Modifiers pass through by grantId; mutations sum then one FA10.
/// Presents are GUI-only (IDamageFxSink after FA10 drain). Nested Flush is a no-op;
/// leftover Enqueue from OnDeath is drained in the same depth-0 window.
/// </summary>
public sealed class EffectFunnel
{
    readonly EffectBag _bag;
    readonly IEffectActionSink _sink;
    IDamageFxSink _fx;
    readonly Dictionary<string, EffectGrantDto> _modifiers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MutationSlot> _mutations = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, DamageFxDto> _presents = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> _skipped = new();
    readonly List<EffectActionPlanItem> _lastFlushed = new();
    int _flushDepth;
    int _flushSeq;
    int _flushCalls;

    public EffectFunnel(EffectBag bag, IDamageFxSink? fxSink = null)
    {
        _bag = bag ?? throw new ArgumentNullException(nameof(bag));
        _sink = bag.Sink;
        _fx = fxSink ?? NoopDamageFxSink.Instance;
        bag.AttachFunnel(this);
    }

    public void AttachFxSink(IDamageFxSink? sink) =>
        _fx = sink ?? NoopDamageFxSink.Instance;

    public IReadOnlyList<string> LastSkipped => _skipped;
    public IReadOnlyList<EffectActionPlanItem> LastFlushedActions => _lastFlushed;
    public int FlushCallCount => _flushCalls;
    public bool HasPending => _modifiers.Count > 0 || _mutations.Count > 0 || _presents.Count > 0;

    internal void NoteOverlayDamage(
        string? actorPtr,
        string targetPtr,
        long amount,
        int chainDepth,
        string? sourceGrantId,
        EffectEventDto? ev) =>
        _bag.NoteOverlayDamage(actorPtr, targetPtr, amount, chainDepth, sourceGrantId, ev);

    public bool Enqueue(RpgEffectEvent ev)
    {
        if (ev == null) throw new ArgumentNullException(nameof(ev));
        if (ev.Family == RpgEffectFamily.Modifier)
        {
            return EnqueueModifier(new EffectGrantDto
            {
                GrantId = ev.GrantId ?? "",
                EffectId = ev.EffectId ?? "",
                OwnerKey = string.IsNullOrWhiteSpace(ev.OwnerKey) ? EffectOwnerKeys.Match : ev.OwnerKey,
                PluginId = ev.PluginId ?? "funnel",
                Overlay = ev.Overlay
            });
        }

        return EnqueueMutation(
            ev.TargetKey ?? "",
            ev.Amount,
            pluginId: ev.PluginId,
            effectId: ev.EffectId,
            grantId: ev.GrantId,
            channel: ev.Channel,
            mode: ev.Mode,
            extra: ev.Overlay);
    }

    public bool EnqueueModifier(EffectGrantDto grant)
    {
        if (grant == null) throw new ArgumentNullException(nameof(grant));
        if (string.IsNullOrWhiteSpace(grant.GrantId))
        {
            _skipped.Add(":missing-grantId");
            return false;
        }

        var id = grant.GrantId;
        if (_modifiers.Count >= ResourceDeltaMath.MailboxCap && !_modifiers.ContainsKey(id))
        {
            _skipped.Add(id + ":mailbox-cap");
            return false;
        }

        _modifiers[id] = grant;
        return true;
    }

    public bool EnqueueMutation(
        string targetKey,
        long amount,
        string? pluginId = null,
        string? effectId = null,
        string? grantId = null,
        string channel = "hp",
        string? mode = null,
        Dictionary<string, object?>? extra = null,
        List<ElementPayloadComponentDto>? elements = null)
    {
        if (!TryGuardMutation(targetKey, amount, channel, mode, extra, out var reason))
        {
            _skipped.Add((grantId ?? targetKey ?? "") + ":" + reason);
            return false;
        }

        var key = MutationKey(targetKey);
        if (_mutations.Count >= ResourceDeltaMath.MailboxCap && !_mutations.ContainsKey(key))
        {
            _skipped.Add(key + ":mailbox-cap");
            return false;
        }

        if (!_mutations.TryGetValue(key, out var slot))
        {
            slot = new MutationSlot
            {
                TargetKey = StatApplyScope.Normalize(targetKey),
                PluginId = pluginId ?? "funnel",
                EffectId = string.IsNullOrWhiteSpace(effectId) ? "funnel.resource_delta" : effectId,
                GrantId = string.IsNullOrWhiteSpace(grantId) ? "funnel:" + key : grantId
            };
            _mutations[key] = slot;
        }

        slot.Amount += amount;
        slot.MergedCount++;
        if (elements is { Count: > 0 })
            slot.Elements = elements;
        return true;
    }

    public bool Withdraw(string grantId) => _bag.Withdraw(grantId);

    /// <summary>Withdraw every grant <paramref name="pluginId"/> issued. <paramref name="ownerKey"/> is
    /// an optional narrowing to ONE owner scope; omitted, this means every scope the plugin granted at
    /// — never "the match scope", which is what it silently meant before. A plugin whose grant is not
    /// <c>match</c>-keyed (<c>PatronSecondaryPlugin</c>'s side-wide <c>plant:*</c> aura is the first)
    /// passed no key from <c>OnRemoved</c>, so its grant could not be found and outlived the match.</summary>
    public int WithdrawByPluginId(string pluginId, string? ownerKey = null)
    {
        var candidates = string.IsNullOrWhiteSpace(ownerKey)
            ? _bag.Grants.All()
            : _bag.ForOwner(null, ownerKey);
        var n = 0;
        foreach (var g in candidates
                     .Where(x => string.Equals(x.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            if (_bag.Withdraw(g.GrantId)) n++;
        }

        return n;
    }

    public bool EnqueuePresent(DamageFxDto fx)
    {
        if (fx == null) throw new ArgumentNullException(nameof(fx));
        if (string.IsNullOrWhiteSpace(fx.TargetPtr))
        {
            _skipped.Add(":missing-target");
            return false;
        }

        var key = PresentKey(fx.TargetPtr, fx.Tag);
        if (!IsDefaultTag(fx.Tag))
            EvictDefaultPresents(fx.TargetPtr);

        if (_presents.Count >= ResourceDeltaMath.MailboxCap && !_presents.ContainsKey(key))
        {
            _skipped.Add(key + ":mailbox-cap");
            return false;
        }

        if (_presents.TryGetValue(key, out var slot))
        {
            slot.Amount += fx.Amount;
            slot.MergedCount += Math.Max(1, fx.MergedCount);
            if (!string.IsNullOrWhiteSpace(fx.Fx)) slot.Fx = fx.Fx;
            if (fx.Elements is { Count: > 0 }) slot.Elements = fx.Elements;
        }
        else
        {
            _presents[key] = new DamageFxDto
            {
                TargetPtr = PtrHex(fx.TargetPtr),
                Amount = fx.Amount,
                Tag = fx.Tag,
                Fx = string.IsNullOrWhiteSpace(fx.Fx) ? "float" : fx.Fx,
                MergedCount = Math.Max(1, fx.MergedCount),
                Elements = fx.Elements is { Count: > 0 } ? fx.Elements : null
            };
        }

        return true;
    }

    /// <summary>Grant queued modifiers then execute one FA10 per mutation key. Nested calls are no-ops.</summary>
    public void Flush()
    {
        _flushCalls++;
        if (_flushDepth > 0) return;
        using var _perf = FusionRpg.Core.Diagnostics.PerfProbe.Measure(FusionRpg.Core.Diagnostics.PerfSection.FunnelFlush);
        _flushDepth++;
        try
        {
            _lastFlushed.Clear();
            var rounds = 0;
            while ((_modifiers.Count > 0 || _mutations.Count > 0) &&
                   rounds++ < ResourceDeltaMath.MailboxCap)
                DrainOnce();
            DrainPresents();
        }
        finally
        {
            _flushDepth--;
        }
    }

    /// <summary>OnEvent copies telemetry then clears so the next window does not duplicate skips.</summary>
    public void AcknowledgeWindow()
    {
        _skipped.Clear();
        _lastFlushed.Clear();
    }

    void DrainOnce()
    {
        var mods = _modifiers.Values.ToList();
        foreach (var grant in mods)
            _modifiers.Remove(grant.GrantId);
        foreach (var grant in mods)
            _bag.Grant(grant);

        var keys = _mutations.Keys.ToList();
        var slots = new List<MutationSlot>(keys.Count);
        foreach (var k in keys)
        {
            slots.Add(_mutations[k]);
            _mutations.Remove(k);
        }

        foreach (var slot in slots)
            ExecuteMutation(slot);
    }

    void DrainPresents()
    {
        var keys = _presents.Keys.ToList();
        var tagged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in keys)
        {
            var fx = _presents[k];
            if (!IsDefaultTag(fx.Tag))
                tagged.Add(PtrHex(fx.TargetPtr));
        }

        var items = new List<DamageFxDto>(keys.Count);
        foreach (var k in keys)
        {
            var fx = _presents[k];
            _presents.Remove(k);
            if (IsDefaultTag(fx.Tag) && tagged.Contains(PtrHex(fx.TargetPtr)))
                continue;
            items.Add(fx);
        }

        foreach (var fx in items)
            _fx.Show(fx);
    }

    static string MutationKey(string targetKey) =>
        StatApplyScope.Normalize(targetKey) + "|ResourceDelta|hp";

    static bool TryGuardMutation(
        string? targetKey,
        long amount,
        string? channel,
        string? mode,
        Dictionary<string, object?>? extra,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(targetKey))
        {
            reason = "missing-target";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(channel) &&
            !string.Equals(channel, "hp", StringComparison.OrdinalIgnoreCase))
        {
            reason = "channel";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(mode) &&
            !string.Equals(mode, "add", StringComparison.OrdinalIgnoreCase))
        {
            reason = "mode-set";
            return false;
        }

        if (ResourceDeltaMath.ExceedsAmountCap(amount))
        {
            reason = "amount-cap";
            return false;
        }

        if (extra != null)
        {
            foreach (var kv in extra)
            {
                if (kv.Key.Equals("absoluteHp", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("setHp", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("hp", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("EntityFinal.Hp", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("entityFinalHp", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "absolute-hp";
                    return false;
                }
            }
        }

        reason = "";
        return true;
    }

    void ExecuteMutation(MutationSlot slot)
    {
        if (ResourceDeltaMath.ExceedsAmountCap(slot.Amount))
        {
            _skipped.Add(slot.GrantId + ":amount-cap");
            return;
        }

        if (slot.MergedCount <= 0 || slot.Amount == 0)
            return;

        _flushSeq++;
        var grant = new EffectGrant
        {
            GrantId = slot.GrantId,
            EffectId = slot.EffectId,
            PluginId = slot.PluginId,
            OwnerKey = slot.TargetKey.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)
                ? slot.TargetKey
                : EffectOwnerKeys.Entity(slot.TargetKey)
        };
        var ptr = slot.TargetKey.StartsWith("entity:", StringComparison.OrdinalIgnoreCase)
            ? slot.TargetKey[7..]
            : slot.TargetKey;
        var ev = new EffectEventDto
        {
            Trigger = "FunnelFlush",
            TargetPtr = ptr,
            Tick = _flushSeq
        };
        var item = new EffectActionPlanItem
        {
            Seq = 0,
            Action = EffectActions.ApplyResourceDelta,
            EffectId = slot.EffectId,
            GrantId = slot.GrantId,
            SourceTag = "funnel:" + slot.EffectId,
            Params = new Dictionary<string, object?>
            {
                ["channel"] = "hp",
                ["amount"] = slot.Amount,
                ["mergedCount"] = slot.MergedCount,
                ["targetPtr"] = ptr
            },
            Tags = new Dictionary<string, string>
            {
                ["effect_id"] = slot.EffectId,
                ["grant_id"] = slot.GrantId,
                ["plugin"] = slot.PluginId,
                ["mergedCount"] = slot.MergedCount.ToString()
            }
        };
        var ctx = new EffectExecuteContext { Event = ev, Grant = grant, Def = new EffectDef { EffectId = slot.EffectId } };
        _lastFlushed.Add(item);
        _sink.Execute(ctx, item);
        EnqueueDefaultPresent(ptr, slot.Amount, slot.MergedCount, slot.Elements);
    }

    void EnqueueDefaultPresent(string ptr, long amount, int mergedCount, List<ElementPayloadComponentDto>? elements)
    {
        var hex = PtrHex(ptr);
        var prefix = hex + "|";
        foreach (var k in _presents.Keys)
        {
            if (k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return;
        }

        EnqueuePresent(new DamageFxDto
        {
            TargetPtr = hex,
            Amount = amount,
            Tag = amount < 0 ? DamageFxTag.Neutral : DamageFxTag.Heal,
            MergedCount = mergedCount,
            Elements = elements
        });
    }

    void EvictDefaultPresents(string targetPtr)
    {
        _presents.Remove(PresentKey(targetPtr, DamageFxTag.Neutral));
        _presents.Remove(PresentKey(targetPtr, DamageFxTag.Heal));
    }

    static bool IsDefaultTag(DamageFxTag tag) =>
        tag is DamageFxTag.Neutral or DamageFxTag.Heal;

    static string PresentKey(string targetPtr, DamageFxTag tag) =>
        PtrHex(targetPtr) + "|" + tag;

    static string PtrHex(string targetPtr) => CombatPtr.Normalize(targetPtr);

    sealed class MutationSlot
    {
        public string TargetKey { get; init; } = "";
        public string PluginId { get; init; } = "";
        public string EffectId { get; init; } = "";
        public string GrantId { get; set; } = "";
        public long Amount { get; set; }
        public int MergedCount { get; set; }
        public List<ElementPayloadComponentDto>? Elements { get; set; }
    }
}
