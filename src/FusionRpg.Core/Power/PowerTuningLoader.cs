using System.Text.Json;

namespace FusionRpg.Core.Power;

/// <summary>
/// Pure parser over a power-scale tuning JSON string — no file I/O (tunables-ssot.md §7.2: "Core
/// never reads a file. Hosts load and inject."). The host reads `data/tuning/power-scale.v{n}.json`
/// and calls <see cref="Parse"/>; tests construct a JSON string inline.
/// </summary>
public static class PowerTuningLoader
{
    public static PowerTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, "power tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var curveEl = Obj(root, "curve", "$");
            var cMilli = Long(curveEl, "cMilli", "curve");
            var bMilli = Long(curveEl, "bMilli", "curve");
            var pinIndex = Int(curveEl, "pinIndex", "curve");
            var pinValue = Long(curveEl, "pinValue", "curve");

            var weightsEl = Obj(root, "weights", "$");
            var wd = Long(weightsEl, "WdMilli", "weights");
            var wa = Long(weightsEl, "WaMilli", "weights");
            var wr = Long(weightsEl, "WrMilli", "weights");
            var wz = Long(weightsEl, "WzMilli", "weights");
            var wm = NullableLong(weightsEl, "WmMilli", "weights");
            var ww = Long(weightsEl, "WwMilli", "weights");
            var wf = Long(weightsEl, "WfMilli", "weights");

            // "channels" is optional — absent means no channel loaded yet (T1.1 predates T2.1), not
            // a rejection. When present, every entry must be well-formed; a malformed channel is a
            // TuningMissing rejection like any other malformed field, never silently skipped.
            Dictionary<string, PowerChannelTuning>? channels = null;
            if (root.TryGetProperty("channels", out var channelsEl) && channelsEl.ValueKind == JsonValueKind.Object)
            {
                channels = new Dictionary<string, PowerChannelTuning>(StringComparer.Ordinal);
                foreach (var prop in channelsEl.EnumerateObject())
                {
                    var chCMilli = Long(prop.Value, "cMilli", $"channels.{prop.Name}");
                    var chPinValue = Long(prop.Value, "pinValue", $"channels.{prop.Name}");
                    channels[prop.Name] = new PowerChannelTuning(chCMilli, chPinValue);
                }
            }

            return PowerTuning.Build(schemaVersion, version, cMilli, bMilli, pinIndex, pinValue,
                wd, wa, wr, wz, wm, ww, wf, channels);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long Long(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static long? NullableLong(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el))
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: missing '{path}.{key}'");
        if (el.ValueKind == JsonValueKind.Null) return null;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new PowerTuningRejection(PowerRejectionReason.TuningMissing, $"power tuning: '{path}.{key}' must be an integer or null");
        return v;
    }
}
