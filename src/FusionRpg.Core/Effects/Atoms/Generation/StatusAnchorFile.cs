using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>One status-anchor row: the authored bases for a <c>status.apply</c> family, plus the
/// file-level ladder ratios (copied per row so the expander never reaches past the row).</summary>
public sealed record StatusAnchorRow(
    string FamilyId, long ChanceT1Permille, long DurationT1Ms, string StatusId,
    long ChanceRatioPermille, long DurationRatioPermille, string TriggerId);

/// <summary>
/// Pure parser for <c>data/seed/items/_registry/status-anchor.v{n}.json</c> — chance/duration t1
/// bases plus the family→status mapping (authored in atom-family-library.md SS3.4, quoted here,
/// never inferred). Data in, rows out: no dispatch, no validation, no compose knowledge.
/// </summary>
public static class StatusAnchorFile
{
    public static IReadOnlyDictionary<string, StatusAnchorRow> Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("defaults", out var defEl) || defEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("status-anchor: missing object 'defaults'");
        var chanceT1 = Long(defEl, "chanceT1Permille");
        var durationT1 = Long(defEl, "durationT1Ms");
        // status.apply REQUIRES a trigger (AtomRowValidator), and which event applies an affliction is
        // content, not expander logic — so it is authored here. Missing = throw (T5), never a default.
        var triggerId = Str(defEl, "triggerId");

        var overrides = new Dictionary<string, (long Chance, long Duration)>(StringComparer.Ordinal);
        if (root.TryGetProperty("overrides", out var ovEl) && ovEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in ovEl.EnumerateObject())
            {
                overrides[p.Name] = (
                    p.Value.TryGetProperty("chanceT1Permille", out var c) ? c.GetInt64() : chanceT1,
                    p.Value.TryGetProperty("durationT1Ms", out var d) ? d.GetInt64() : durationT1);
            }
        }

        if (!root.TryGetProperty("familyStatus", out var fsEl) || fsEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("status-anchor: missing object 'familyStatus'");

        // Ladder ratios are authored tuning (registry twoLadderRule shape), never code consts —
        // a balance number as a const is audit M2. Missing = throw (T5), never a default.
        if (!root.TryGetProperty("ratios", out var ratiosEl) || ratiosEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("status-anchor: missing object 'ratios'");
        var chanceRatio = Long(ratiosEl, "chanceRatioPermille");
        var durationRatio = Long(ratiosEl, "durationRatioPermille");

        var rows = new Dictionary<string, StatusAnchorRow>(StringComparer.Ordinal);
        foreach (var p in fsEl.EnumerateObject())
        {
            var status = p.Value.GetString();
            if (string.IsNullOrEmpty(status))
                throw new FormatException($"status-anchor: familyStatus['{p.Name}'] must be a status id string");
            var (ch, du) = overrides.TryGetValue(p.Name, out var ov) ? ov : (chanceT1, durationT1);
            rows[p.Name] = new StatusAnchorRow(p.Name, ch, du, status, chanceRatio, durationRatio, triggerId);
        }

        return rows;
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"status-anchor: missing string 'defaults.{key}'");
        var v = el.GetString();
        if (string.IsNullOrEmpty(v))
            throw new FormatException($"status-anchor: 'defaults.{key}' must not be empty");
        return v;
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new FormatException($"status-anchor: missing integer 'defaults.{key}'");
        return v;
    }
}
