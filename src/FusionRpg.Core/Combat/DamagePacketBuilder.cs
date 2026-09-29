using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms.Power;

namespace FusionRpg.Core.Combat;

/// <summary>Parse grant overlay / debug body into a planning <see cref="DamagePacket"/>.</summary>
public static class DamagePacketBuilder
{
    public static DamagePacket FromOverlay(
        Dictionary<string, object?>? overlay,
        EffectEventDto? ev = null,
        string? grantId = null,
        string? effectId = null,
        string? pluginId = null,
        long? amountOverride = null)
    {
        overlay ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var amount = amountOverride ?? ResolveAmount(overlay, ev);
        var packet = new DamagePacket
        {
            PacketId = Guid.NewGuid().ToString("N"),
            SourceGrantId = grantId ?? "",
            EffectId = effectId,
            PluginId = pluginId,
            ActorPtr = ev?.ActorPtr,
            SignedAmount = amount,
            Channel = JsonOverlay.GetString(overlay, "channel") ?? "hp",
            Tick = ev?.Tick ?? 0,
            ChainDepth = overlay.ContainsKey("chainDepth")
                ? JsonOverlay.GetInt(overlay, "chainDepth")
                : (ev?.ChainDepth ?? 0),
            ProcDepthLimit = overlay.ContainsKey("procDepthLimit")
                ? JsonOverlay.GetInt(overlay, "procDepthLimit")
                : null,
            Target = ParseTargetFromOverlay(overlay),
            Delivery = ParseDelivery(overlay)
        };

        if (overlay.TryGetValue("burst", out var burstObj) && burstObj != null)
        {
            var burstMap = JsonOverlay.FromObject(burstObj);
            packet.Burst = FromOverlay(burstMap, ev, grantId, effectId, pluginId);
            packet.Burst.ChainDepth = packet.ChainDepth + 1;
        }

        packet.ElementPayload = ParseElementPayload(overlay);

        return packet;
    }

    /// <summary>
    /// `P0.2` (spec-value-spec-and-curve.md "Event-linked magnitudes"): the compiled "amount" is
    /// either a plain number (the normal case, unchanged) or the marker
    /// <c>{"eventField": "damage", "multiplierMilli": ...}</c> `AtomCompiler.ResolvedParams` bakes for
    /// an event-linked spec. The marker survives either as an in-memory <see
    /// cref="Dictionary{TKey,TValue}"/> (no JSON round trip, e.g. a test building the grant directly)
    /// or as a <see cref="JsonElement"/> object (after a real JSON round trip through E19) —
    /// <see cref="JsonOverlay.FromObject"/> already normalises both, the same way every other overlay
    /// object value in this codebase is read.
    /// </summary>
    static long ResolveAmount(Dictionary<string, object?> overlay, EffectEventDto? ev)
    {
        if (overlay.TryGetValue("amount", out var raw) && IsObjectShape(raw))
        {
            var marker = JsonOverlay.FromObject(raw);
            if (JsonOverlay.GetString(marker, "eventField") is { } field)
            {
                // lawn-hit-entry (T9c): an instakill-shaped hit (lawnmower / board-wipe — see
                // GameEventRec.InstakillShaped / EffectEventDto.InstakillShaped) must not scale a
                // rider off its own magnitude — a 1,000,000-damage lawnmower event would otherwise
                // pass straight through this multiplier into a rider of the same magnitude.
                // Deliberately a DamageType-driven refusal, never a numeric clamp (a clamp is a
                // hard ceiling a balance pass would eventually cross legitimately — AGENTS.md "No
                // hard progression ceilings").
                if (ev?.InstakillShaped == true)
                    return 0;

                var multiplierMilli = JsonOverlay.GetInt(marker, "multiplierMilli", 1000);
                // Absent damage (no combat event, or an event with none) heals nothing rather than
                // throwing — the same "never crash the hot path" rule every other overlay read follows.
                var fieldValue = field switch
                {
                    // Magnitude: producers disagree on Damage's sign (lawn EventDrain -|d|, battle and
                    // overlay procs +|d|). The authored multiplierMilli carries the sign: negative
                    // damages, positive heals ("500" = 50% lifesteal). lawn-combat-wire L-N19.
                    "damage" => Math.Abs(ev?.Damage ?? 0),
                    _ => 0,
                };
                return PowerMath.DivRound(fieldValue * multiplierMilli, PowerMath.One);
            }
        }

        return (long)JsonOverlay.GetDouble(overlay, "amount");
    }

    static bool IsObjectShape(object? v) =>
        v is Dictionary<string, object?> or Dictionary<string, object>
        || v is JsonElement { ValueKind: JsonValueKind.Object };

    static List<ElementPayloadComponentDto>? ParseElementPayload(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("elementPayload", out var raw) || raw == null)
            return null;

        var items = raw as IList<object?> ?? (raw is IEnumerable<object?> en ? en.ToList() : null);
        if (items == null || items.Count == 0)
            return null;

        var list = new List<ElementPayloadComponentDto>();
        foreach (var item in items)
        {
            var map = JsonOverlay.FromObject(item);
            var element = JsonOverlay.GetString(map, "element");
            if (string.IsNullOrWhiteSpace(element)) continue;
            list.Add(new ElementPayloadComponentDto
            {
                Element = element,
                Weight = JsonOverlay.GetDouble(map, "weight", 1.0)
            });
        }

        return list.Count > 0 ? list : null;
    }

    public static TargetSpec ParseTargetFromOverlay(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("target", out var t) || t == null)
        {
            var ptr = JsonOverlay.GetString(overlay, "targetPtr");
            return new TargetSpec
            {
                Mode = string.IsNullOrWhiteSpace(ptr) ? TargetModes.EventTarget : TargetModes.Single,
                Ptr = ptr
            };
        }

        var map = JsonOverlay.FromObject(t);
        return new TargetSpec
        {
            Mode = JsonOverlay.GetString(map, "mode") ?? TargetModes.EventTarget,
            Ptr = JsonOverlay.GetString(map, "ptr"),
            Count = map.ContainsKey("count") ? JsonOverlay.GetInt(map, "count") : null,
            Shape = JsonOverlay.GetString(map, "shape"),
            Size = map.ContainsKey("size") ? JsonOverlay.GetInt(map, "size") : null,
            Width = map.ContainsKey("width") ? JsonOverlay.GetInt(map, "width") : null,
            Height = map.ContainsKey("height") ? JsonOverlay.GetInt(map, "height") : null,
            Anchor = map.TryGetValue("anchor", out var a) ? a : null,
            AnchorOrigin = JsonOverlay.GetString(map, "anchorOrigin"),
            Filters = map.TryGetValue("filters", out var f) && f != null
                ? JsonOverlay.FromObject(f)
                : null,
            MaxTargets = map.ContainsKey("maxTargets") ? JsonOverlay.GetInt(map, "maxTargets") : null
        };
    }

    static DeliverySpec ParseDelivery(Dictionary<string, object?> overlay)
    {
        if (!overlay.TryGetValue("delivery", out var d) || d == null)
            return new DeliverySpec { Mode = DeliveryModes.Instant };

        var map = JsonOverlay.FromObject(d);
        return new DeliverySpec
        {
            Mode = JsonOverlay.GetString(map, "mode") ?? DeliveryModes.Instant,
            PeriodMs = map.ContainsKey("periodMs") ? JsonOverlay.GetInt(map, "periodMs") : null,
            DurationMs = map.ContainsKey("durationMs") ? JsonOverlay.GetInt(map, "durationMs") : null,
            TickBudget = map.ContainsKey("tickBudget") ? JsonOverlay.GetInt(map, "tickBudget") : null,
            EveryHits = map.ContainsKey("everyHits") ? JsonOverlay.GetInt(map, "everyHits") : null,
            ResetOnBurst = !map.ContainsKey("resetOnBurst") || JsonOverlay.GetBool(map, "resetOnBurst", true),
            CounterScope = JsonOverlay.GetString(map, "counterScope")
        };
    }
}
