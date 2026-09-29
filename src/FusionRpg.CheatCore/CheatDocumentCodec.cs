using FusionRpg.Contracts;

namespace FusionRpg.CheatCore;

/// <summary>entries[] wire format ↔ ModDocument for RPG / future consumers.</summary>
public static class CheatDocumentCodec
{
    public static ModDocument FromEntries(
        long revision,
        string source,
        IEnumerable<(string id, bool enabled, double floatValue, string? kind)> entries,
        string updatedAt)
    {
        var doc = new ModDocument
        {
            Revision = revision,
            Source = source,
            UpdatedAt = updatedAt
        };
        foreach (var (id, enabled, floatValue, kind) in entries)
        {
            if (CheatSchema.ShouldStripFromDocument(id, enabled, floatValue, kind))
                continue;
            var meta = CheatSchema.Get(id);
            doc.Mods.Add(new ModEntry
            {
                Id = id,
                Channel = meta?.Channel ?? "",
                Op = meta?.Role == CheatFieldRole.Toggle ? "Toggle" : (meta?.Op ?? ""),
                Value = meta?.Role == CheatFieldRole.Toggle ? (enabled ? 1 : 0) : floatValue,
                Enabled = enabled
            });
        }
        return doc;
    }

    public static List<(string id, bool enabled, double floatValue, string kind)> ToEntries(ModDocument doc)
    {
        var list = new List<(string, bool, double, string)>();
        foreach (var m in doc.Mods)
        {
            if (string.IsNullOrWhiteSpace(m.Id)) continue;
            var meta = CheatSchema.Get(m.Id);
            var kind = meta?.Kind ?? "number";
            if (meta?.Role == CheatFieldRole.Toggle)
            {
                var on = m.Enabled && (m.Value is null or > 0);
                if (CheatSchema.ShouldStripFromDocument(m.Id, on, 0, kind))
                    continue;
                list.Add((m.Id, on, 0, kind));
            }
            else
            {
                var fv = m.Value ?? 0;
                if (CheatSchema.ShouldStripFromDocument(m.Id, m.Enabled, fv, kind))
                    continue;
                list.Add((m.Id, m.Enabled, fv, kind));
            }
        }
        return list;
    }
}

/// <summary>Pure predicates shared by injector apply paths (unit-testable).</summary>
public static class CheatPresentRules
{
    public static bool HasNonIdentityScale(bool isSet, string id, double value)
    {
        if (!isSet) return false;
        if (CheatSchema.TryGet(id, out var meta))
        {
            if (meta.Role == CheatFieldRole.ScalePercent)
                return Math.Abs(value - 1d) > 0.0001;
            if (meta.Role == CheatFieldRole.ScaleFlat)
                return Math.Abs(value) > 0.0001;
        }
        return !CheatSchema.IsUnsetOrIdentity(id, true, value);
    }

    public static bool ShouldApplyAbsolute(bool isSet, double value) =>
        isSet && value > 0;

    public static bool ShouldApplyBoardField(bool isSet) => isSet;
}
