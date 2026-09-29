using FusionRpg.Contracts;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Status;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Status;

/// <summary>Dispatch status PulseHp through L3 Instant → Funnel.</summary>
public sealed class StatusFunnelPulseSink : IStatusPulseSink
{
    readonly BoardSnapshot _board;
    readonly EffectEventDto _eventTemplate;
    readonly EffectFunnel? _funnel;
    readonly CombatPolicy _policy;
    readonly ICombatRng _rng;
    readonly ICombatMath _math;
    readonly List<string> _skipped;
    readonly string? _effectId;
    readonly string? _pluginId;
    readonly Combat.Shield.ShieldGate? _shieldGate;
    readonly CombatActorResolve? _actorResolve;
    readonly Action<DamageApplyResult, DamageOrigin, IReadOnlyList<ElementPayloadComponent>, string?>? _onDamageApplied;

    public StatusFunnelPulseSink(
        BoardSnapshot board,
        EffectEventDto eventTemplate,
        EffectFunnel? funnel,
        CombatPolicy policy,
        ICombatRng rng,
        ICombatMath math,
        List<string> skipped,
        string? effectId,
        string? pluginId,
        Combat.Shield.ShieldGate? shieldGate = null,
        CombatActorResolve? actorResolve = null,
        Action<DamageApplyResult, DamageOrigin, IReadOnlyList<ElementPayloadComponent>, string?>? onDamageApplied = null)
    {
        _shieldGate = shieldGate;
        _actorResolve = actorResolve;
        _board = board;
        _eventTemplate = eventTemplate;
        _funnel = funnel;
        _policy = policy;
        _rng = rng;
        _math = math;
        _skipped = skipped;
        _effectId = effectId;
        _pluginId = pluginId;
        _onDamageApplied = onDamageApplied;
    }

    public void PulseHp(StatusInstance instance, double amount)
    {
        var packet = new DamagePacket
        {
            PacketId = Guid.NewGuid().ToString("N"),
            SourceGrantId = instance.GrantId,
            EffectId = instance.EffectId ?? _effectId,
            PluginId = instance.PluginId ?? _pluginId,
            ActorPtr = instance.AttackerPtr,
            SignedAmount = (long)Math.Round(amount),
            Channel = "hp",
            ChainDepth = _eventTemplate.ChainDepth + 1,
            Target = new TargetSpec { Mode = TargetModes.Single, Ptr = instance.HostPtr },
            Delivery = new DeliverySpec { Mode = DeliveryModes.Instant },
            // Wave E1: the same StatusPulsePayload rule the battle sink uses -- one function, so the
            // two modes cannot drift on a parity invariant the program states explicitly. Null for an
            // untyped status, which is byte-identical to the pre-E1 packet.
            ElementPayload = instance.Element is { } el
                ? new List<ElementPayloadComponentDto> { new() { Element = el.ToElementId(), Weight = 1.0 } }
                : null
        };

        var ev = new EffectEventDto
        {
            Trigger = EffectTriggers.OnTimer,
            MatchKey = _eventTemplate.MatchKey,
            Side = _eventTemplate.Side,
            ActorPtr = instance.AttackerPtr ?? _eventTemplate.ActorPtr,
            TargetPtr = instance.HostPtr,
            TypeId = _eventTemplate.TypeId,
            TargetTypeId = _eventTemplate.TargetTypeId,
            Tick = _eventTemplate.Tick,
            ScenarioId = _eventTemplate.ScenarioId,
            ChainDepth = packet.ChainDepth
        };

        // spec-gate-counters.md §2.2d / §7 P1 -- the discriminator ElementMasteryCounter needs:
        // a DoT tick reaches the SAME apply tail a direct hit does, and until this line the pipeline
        // had no way to tell them apart (every caller defaulted to DirectHit). Crediting a pulse would
        // let one applied status earn an elemental credit every tick for its whole duration, double-
        // paying for the one application `status_applied` already credited -- this is the live-lawn
        // half of that fix (the battle/sim engine's OWN separate pulse sink, `BattleEngine.cs`'s
        // `BattlePulseSink`, calls `DamageApplyPipeline.Apply` directly rather than through this
        // dispatcher, and is untouched here -- it is under concurrent edit by another session, R9).
        CombatDamageDispatcher.DispatchInstant(
            packet, _board, ev, _funnel, _policy, _rng, _math, _skipped, _shieldGate, _actorResolve,
            origin: DamageOrigin.StatusPulse, onDamageApplied: _onDamageApplied);
    }

    /// <summary>
    /// Leech's heal half (spec-healing-pair.md §3). A SEPARATE <see cref="DamagePacket"/>, targeting
    /// the attacker rather than <see cref="StatusInstance.HostPtr"/> — mirrors <see cref="PulseHp"/>'s
    /// shape exactly except <c>Target</c>/<c>ActorPtr</c> both point at the healer. The positive
    /// <see cref="DamagePacket.SignedAmount"/> reaches <see cref="OverlayCombatMath.Finalize"/>'s heal
    /// branch through the SAME dispatch path as any other heal, so <c>combat.heal.power</c> applies for
    /// free — this method does not re-implement spec-healing-pair.md §2.1's formula, it only supplies
    /// <c>baseOverlayHeal</c> (the absolute damage dealt) as the packet's starting amount.
    /// </summary>
    public void PulseHealAttacker(StatusInstance instance, double baseHealAmount)
    {
        var packet = new DamagePacket
        {
            PacketId = Guid.NewGuid().ToString("N"),
            SourceGrantId = instance.GrantId,
            EffectId = instance.EffectId ?? _effectId,
            PluginId = instance.PluginId ?? _pluginId,
            ActorPtr = instance.AttackerPtr,
            SignedAmount = (long)Math.Round(Math.Abs(baseHealAmount)),
            Channel = "hp",
            ChainDepth = _eventTemplate.ChainDepth + 1,
            Target = new TargetSpec { Mode = TargetModes.Single, Ptr = instance.AttackerPtr },
            Delivery = new DeliverySpec { Mode = DeliveryModes.Instant }
        };

        var ev = new EffectEventDto
        {
            Trigger = EffectTriggers.OnTimer,
            MatchKey = _eventTemplate.MatchKey,
            Side = _eventTemplate.Side,
            ActorPtr = instance.AttackerPtr,
            TargetPtr = instance.AttackerPtr,
            TypeId = _eventTemplate.TypeId,
            TargetTypeId = _eventTemplate.TargetTypeId,
            Tick = _eventTemplate.Tick,
            ScenarioId = _eventTemplate.ScenarioId,
            ChainDepth = packet.ChainDepth
        };

        // No ElementPayload on this packet (see the class doc above) -- `origin` is passed for the
        // same reason it is on PulseHp, not because this call site currently produces any credit.
        CombatDamageDispatcher.DispatchInstant(
            packet, _board, ev, _funnel, _policy, _rng, _math, _skipped, _shieldGate, _actorResolve,
            origin: DamageOrigin.StatusPulse, onDamageApplied: _onDamageApplied);
    }
}

public static class StatusEffectBridge
{
    public static bool TryResolveStatusId(
        Dictionary<string, object?> overlay,
        out string statusId)
    {
        statusId = JsonOverlay.GetString(overlay, "statusId") ?? "";
        if (!string.IsNullOrWhiteSpace(statusId))
            return true;

        var delivery = ResolveDeliveryMode(overlay);
        if (string.Equals(delivery, DeliveryModes.OverTime, StringComparison.OrdinalIgnoreCase))
        {
            statusId = "wither";
            return true;
        }

        if (string.Equals(delivery, DeliveryModes.Counter, StringComparison.OrdinalIgnoreCase))
        {
            statusId = "bond";
            return true;
        }

        return false;
    }

    static IReadOnlyList<string> ResolveHostPtrs(
        Dictionary<string, object?> overlay,
        EffectEventDto ev,
        BoardSnapshot board)
    {
        var spec = DamagePacketBuilder.ParseTargetFromOverlay(overlay);
        var mode = spec.Mode ?? TargetModes.EventTarget;
        if (string.Equals(mode, TargetModes.Area, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, TargetModes.All, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, TargetModes.Multi, StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, TargetModes.Random, StringComparison.OrdinalIgnoreCase))
        {
            return TargetResolver.Resolve(spec, board, ev);
        }

        if (string.Equals(mode, TargetModes.Single, StringComparison.OrdinalIgnoreCase))
        {
            var ptr = spec.Ptr ?? ev.TargetPtr;
            return string.IsNullOrWhiteSpace(ptr) ? Array.Empty<string>() : new[] { ptr.Trim() };
        }

        var host = ResolveHostPtr(overlay, ev);
        return string.IsNullOrWhiteSpace(host) ? Array.Empty<string>() : new[] { host };
    }

    static string? ResolveDeliveryMode(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("delivery", out var d) || d == null)
            return null;
        var map = JsonOverlay.FromObject(d);
        return JsonOverlay.GetString(map, "mode");
    }

    static string? ResolveHostPtr(Dictionary<string, object?> overlay, EffectEventDto ev)
    {
        if (overlay.TryGetValue("target", out var t) && t != null)
        {
            var map = JsonOverlay.FromObject(t);
            var mode = JsonOverlay.GetString(map, "mode") ?? TargetModes.EventTarget;
            if (string.Equals(mode, TargetModes.EventTarget, StringComparison.OrdinalIgnoreCase))
                return ev.TargetPtr;
            if (string.Equals(mode, TargetModes.Actor, StringComparison.OrdinalIgnoreCase))
                return ev.ActorPtr;
            if (string.Equals(mode, TargetModes.Single, StringComparison.OrdinalIgnoreCase))
                return JsonOverlay.GetString(map, "ptr") ?? ev.TargetPtr;
        }

        return ev.TargetPtr;
    }

    public static StatusApplyInput BuildApplyInput(
        string statusId,
        EffectGrant grant,
        EffectEventDto ev,
        Dictionary<string, object?> overlay,
        string hostPtr)
    {
        var period = JsonOverlay.GetInt(overlay, "periodMs", 0);
        if (period <= 0)
        {
            var delivery = JsonOverlay.FromObject(overlay.GetValueOrDefault("delivery"));
            period = JsonOverlay.GetInt(delivery, "periodMs", 1000);
        }

        var duration = JsonOverlay.GetInt(overlay, "durationMs", 0);
        if (duration <= 0)
        {
            var delivery = JsonOverlay.FromObject(overlay.GetValueOrDefault("delivery"));
            duration = JsonOverlay.GetInt(delivery, "durationMs", 5000);
        }

        var magnitude = JsonOverlay.GetDouble(overlay, "amount", 0);
        if (Math.Abs(magnitude) < 1e-9)
            magnitude = -Math.Abs(JsonOverlay.GetDouble(overlay, "magnitude", 0));
        var chance = JsonOverlay.GetDouble(overlay, "chance", 1.0);
        var statusIcd = JsonOverlay.GetInt(overlay, "status_icd_ms", 0);
        if (statusIcd <= 0)
            statusIcd = JsonOverlay.GetInt(overlay, "statusIcdMs", 0);

        var tickBudget = JsonOverlay.GetInt(overlay, "tickBudget", 0);
        if (tickBudget <= 0)
        {
            var delivery = JsonOverlay.FromObject(overlay.GetValueOrDefault("delivery"));
            tickBudget = JsonOverlay.GetInt(delivery, "tickBudget", 1);
        }

        var spreadChance = 0.0;
        string? spreadStatusId = null;
        var spreadMaxHops = 0;
        var spreadIcdMs = 0;
        TargetSpec? spreadTarget = null;
        if (overlay.TryGetValue("spread", out var spreadObj) && spreadObj != null)
        {
            var spread = JsonOverlay.FromObject(spreadObj);
            spreadChance = JsonOverlay.GetDouble(spread, "chance", 0);
            spreadStatusId = JsonOverlay.GetString(spread, "statusId") ?? statusId;
            spreadMaxHops = JsonOverlay.GetInt(spread, "maxHops", 0);
            spreadIcdMs = JsonOverlay.GetInt(spread, "icd_ms", 0);
            if (spread.TryGetValue("target", out var targetObj) && targetObj != null)
                spreadTarget = DamagePacketBuilder.ParseTargetFromOverlay(
                    new Dictionary<string, object?> { ["target"] = targetObj });
        }

        var immunityTags = ParseImmunityTags(overlay);

        return new StatusApplyInput(
            statusId,
            hostPtr,
            ev.ActorPtr,
            grant.GrantId,
            magnitude,
            duration,
            period,
            duration,
            statusIcd,
            tickBudget,
            chance,
            grant.EffectId,
            grant.PluginId,
            ImmunityTags: immunityTags,
            SpreadChance: spreadChance,
            SpreadStatusId: spreadStatusId,
            SpreadMaxHops: spreadMaxHops,
            SpreadIcdMs: spreadIcdMs,
            SpreadTarget: spreadTarget,
            StatMods: ParseStatMods(overlay));
    }

    /// <summary>
    /// The <c>stat</c> overlay (E17) — timed modifiers a status contributes while active.
    ///
    /// <para>A malformed block is <b>dropped with a reason</b>, not partially applied: half a stat
    /// payload is a status that does some of what it says. The overlay itself already passed the
    /// allowlist by the time it reaches here, so this is about shape, not permission.</para>
    /// </summary>
    static IReadOnlyList<StatusStatMod>? ParseStatMods(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("stat", out var raw) || raw is null) return null;

        return StatusStatPayload.TryParse(raw, out var mods, out _) && mods.Count > 0 ? mods : null;
    }

    static IReadOnlyList<string>? ParseImmunityTags(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("immunityTags", out var raw) || raw == null)
            return null;

        if (raw is IEnumerable<object> list)
        {
            var tags = list
                .Select(x => x?.ToString()?.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Cast<string>()
                .ToList();
            return tags.Count > 0 ? tags : null;
        }

        if (raw is string single && !string.IsNullOrWhiteSpace(single))
            return new[] { single.Trim() };

        return null;
    }

    public static bool TryApplyFromGrant(
        StatusRuntime runtime,
        EffectGrant grant,
        EffectEventDto ev,
        Dictionary<string, object?> overlay,
        BoardSnapshot board,
        IStatusRng rng,
        DateTimeOffset now,
        List<string> skipped,
        List<EffectActionPlanItem>? plannedOut = null,
        EffectDef? effectDef = null)
    {
        if (!TryResolveStatusId(overlay, out var statusId))
            return false;

        StatusDef statusDef;
        try
        {
            statusDef = runtime.Catalog.GetRequired(statusId);
        }
        catch (UnknownStatusIdException)
        {
            skipped.Add(grant.GrantId + ":unknown-statusId");
            return true;
        }

        // C2 (completeness-audit.md): an overlay carrying `stat` on a status that never declared
        // ModifyStat parsed and validated silently before this check — the exact "documentation of a
        // capability that did not exist" shape E17 was written to close for the four statuses that
        // DO declare it. Refuse rather than silently drop the block, matching the
        // unknown-statusId/status-no-target refusals right beside this one.
        if (overlay.TryGetValue("stat", out var statOverlay) && statOverlay is not null
            && !statusDef.PayloadKinds.Contains(StatusPayloadKind.ModifyStat))
        {
            skipped.Add(grant.GrantId + ":status-stat-overlay-without-ModifyStat");
            return true;
        }

        var hostPtrs = ResolveHostPtrs(overlay, ev, board);
        if (hostPtrs.Count == 0)
        {
            skipped.Add(grant.GrantId + ":status-no-target");
            return true;
        }

        foreach (var hostPtr in hostPtrs)
        {
            var input = BuildApplyInput(statusId, grant, ev, overlay, hostPtr);
            var outcome = runtime.Apply(input, rng, now);
            if (!outcome.Applied && outcome.ResistReason != StatusResistReason.StatusIcd)
                skipped.Add(grant.GrantId + ":status-" + (outcome.ResistReason?.ToString() ?? "resisted"));
            else if (!outcome.Applied && outcome.ResistReason == StatusResistReason.StatusIcd)
                skipped.Add(grant.GrantId + ":status-icd");
            else if (outcome.Applied
                     && plannedOut != null
                     && effectDef != null
                     && statusDef.PayloadKinds.Contains(StatusPayloadKind.UnityCc))
            {
                plannedOut.Add(new EffectActionPlanItem
                {
                    Seq = plannedOut.Count,
                    Action = EffectActions.ApplyStatus,
                    EffectId = effectDef.EffectId,
                    GrantId = grant.GrantId,
                    SourceTag = "status:" + statusId,
                    Params = new Dictionary<string, object?>
                    {
                        ["status"] = statusId,
                        ["targetPtr"] = hostPtr,
                        ["duration"] = Math.Max(0.1, outcome.Instance!.EffectiveDuration / 1000.0),
                        ["level"] = 1,
                        ["method"] = true
                    },
                    Tags = new Dictionary<string, string>
                    {
                        ["effect_id"] = effectDef.EffectId,
                        ["grant_id"] = grant.GrantId,
                        ["status_id"] = statusId
                    }
                });
            }
        }

        return true;
    }
}
