using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>E30 (spec-channel-pool.md §3.1): one weighted member of a channel pool. <c>WeightMilli</c>
/// is per-mille and structural to the draw, never a magnitude — the balance surface it belongs to is
/// the pool file itself, not this record.</summary>
public sealed record ChannelPoolMember(string Channel, int WeightMilli);

/// <summary>One authored channel pool — a named, weighted set of channels an atom's <c>params.channel</c>
/// may reference instead of one concrete channel (§3.1). Core-side, no I/O; a <c>FusionRpg.Data</c>
/// persistence row is a separate, later concern this module does not require to satisfy its own
/// acceptance criteria 1-4/6-8 (all provable Core-side, matching this repo's own "seed → concrete"
/// precedent of a fully-tested Core layer shipping ahead of its storage wiring).</summary>
public sealed record ChannelPoolRow(string PoolId, string? Note, IReadOnlyList<ChannelPoolMember> Members);

/// <summary>Pure parser for the <c>channel-pool</c> envelope (<c>gk-data/packs/fusion/data/seed/channel-pools/pools.v1.json</c>,
/// §3.1). No I/O — the caller reads the file, this parses the string, matching every other seed-file
/// parser in this directory (<c>AtomSeedFile</c>, <c>EnablerPayoffPairings</c>).</summary>
public static class ChannelPoolFile
{
    public static AtomRejection TryParse(string json, out IReadOnlyList<ChannelPoolRow> pools)
    {
        pools = Array.Empty<ChannelPoolRow>();

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"channel-pool: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var entriesEl)
                || entriesEl.ValueKind != JsonValueKind.Array)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    "channel-pool: root must be an object with an 'entries' array");

            var list = new List<ChannelPoolRow>();
            var seenIds = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entriesEl.EnumerateArray())
            {
                var read = TryParseEntry(entry, out var row);
                if (!read.IsOk) return read;

                if (!seenIds.Add(row.PoolId))
                    return AtomRejection.Fail(AtomRejectionReason.DuplicateKey, $"channel-pool: duplicate pool id '{row.PoolId}'");

                list.Add(row);
            }

            pools = list;
            return AtomRejection.Ok;
        }
    }

    /// <summary>One <c>entries[]</c> element → one <see cref="ChannelPoolRow"/>. Shared by
    /// <see cref="TryParse"/> (the whole-document form) and <see cref="AtomSeedFile"/>'s own
    /// per-entry sweep dispatch, so the two never drift apart on what a valid pool entry looks like.</summary>
    public static AtomRejection TryParseEntry(JsonElement entry, out ChannelPoolRow row)
    {
        row = new ChannelPoolRow("", null, Array.Empty<ChannelPoolMember>());

        if (!entry.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "channel-pool: an entry is missing its string 'id'");
        var id = idEl.GetString()!;

        var note = entry.TryGetProperty("note", out var noteEl) && noteEl.ValueKind == JsonValueKind.String
            ? noteEl.GetString() : null;

        if (!entry.TryGetProperty("members", out var membersEl) || membersEl.ValueKind != JsonValueKind.Array
            || membersEl.GetArrayLength() == 0)
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"channel-pool '{id}': 'members' must be a non-empty array — §3.3 rule 5, a pool that can draw nothing is a defect");

        var members = new List<ChannelPoolMember>();
        foreach (var m in membersEl.EnumerateArray())
        {
            if (!m.TryGetProperty("channel", out var chEl) || chEl.ValueKind != JsonValueKind.String)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"channel-pool '{id}': a member is missing its string 'channel'");
            if (!m.TryGetProperty("weight", out var wEl) || wEl.ValueKind != JsonValueKind.Number || !wEl.TryGetInt32(out var weight) || weight <= 0)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"channel-pool '{id}': member '{chEl.GetString()}' needs a positive integer 'weight'");
            members.Add(new ChannelPoolMember(chEl.GetString()!, weight));
        }

        row = new ChannelPoolRow(id, note, members);
        return AtomRejection.Ok;
    }
}

/// <summary>E30 §3.2: what an atom's <c>params.channel</c> names — either a concrete channel (the
/// unchanged, pre-existing form) or a reference into a pool, resolved to a concrete channel at roll
/// time (effect-pipeline module 2's job, not this type's).</summary>
public sealed record ChannelRef(string? Concrete, string? PoolId, int Count, bool AllowRepeat)
{
    public bool IsPool => PoolId is not null;

    public static ChannelRef ToConcrete(string channel) => new(channel, null, 1, false);
    public static ChannelRef ToPool(string poolId, int count, bool allowRepeat) => new(null, poolId, count, allowRepeat);
}

/// <summary>Reads a <c>channel</c> param value in either shape it can arrive in: a raw
/// <see cref="JsonElement"/> (validation, straight off <c>AtomRow.ParamsJson</c>) or an already-unwrapped
/// native value (post-compile / post-wire-round-trip — a plain <c>string</c>, or a
/// <c>Dictionary&lt;string, object?&gt;</c> for the pool-object form, both produced by
/// <see cref="AtomCompiler.Plain"/>/<see cref="FusionRpg.Core.Effects.JsonOverlay"/> after this
/// session's own E28 fix to those two methods).</summary>
public static class ChannelRefJson
{
    public static AtomRejection TryRead(object? raw, out ChannelRef channelRef)
    {
        channelRef = ChannelRef.ToConcrete("");

        switch (raw)
        {
            case null:
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "channel: missing value");

            case string s:
                channelRef = ChannelRef.ToConcrete(s);
                return AtomRejection.Ok;

            case JsonElement { ValueKind: JsonValueKind.String } el:
                channelRef = ChannelRef.ToConcrete(el.GetString() ?? "");
                return AtomRejection.Ok;

            case JsonElement { ValueKind: JsonValueKind.Object } el:
                return TryReadPoolObject(
                    Prop(el, "pool"), Prop(el, "count"), Prop(el, "allowRepeat"), out channelRef);

            case Dictionary<string, object?> dict:
                return TryReadPoolObject(
                    dict.TryGetValue("pool", out var p) ? p : null,
                    dict.TryGetValue("count", out var c) ? c : null,
                    dict.TryGetValue("allowRepeat", out var a) ? a : null,
                    out channelRef);

            default:
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"channel: must be a string or a pool reference object, got {raw.GetType().Name}");
        }
    }

    static object? Prop(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) ? v : null;

    static AtomRejection TryReadPoolObject(object? poolRaw, object? countRaw, object? allowRepeatRaw, out ChannelRef channelRef)
    {
        channelRef = ChannelRef.ToConcrete("");

        var poolId = poolRaw switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => null,
        };
        if (string.IsNullOrEmpty(poolId))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "channel: a pool reference needs a string 'pool' id");

        // count defaults to 1, floored at 1 — a structural bound (a draw of zero members is not an
        // effect), per §4's own required comment.
        var count = 1;
        if (countRaw is not null)
        {
            var parsed = countRaw switch
            {
                long l => (int?)l,
                int i => i,
                JsonElement { ValueKind: JsonValueKind.Number } el when el.TryGetInt32(out var n) => n,
                _ => null,
            };
            if (parsed is null)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue, $"channel: pool '{poolId}' has a non-integer 'count'");
            count = parsed.Value;
        }

        var allowRepeat = allowRepeatRaw switch
        {
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            null => false,
            _ => (bool?)null ?? false,
        };

        channelRef = ChannelRef.ToPool(poolId, count, allowRepeat);
        return AtomRejection.Ok;
    }
}
