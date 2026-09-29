using System.Text.Json;

namespace FusionRpg.Core.Items.Gems;

/// <summary>
/// One row of <c>gk-data/packs/fusion/data/seed/items/gems/*.json</c> — the authored seed (`docs/architecture/item/
/// entry-shapes.md` §1), never a magnitude. <c>Family</c>/<c>Element</c>/<c>PowerBand</c> are what a
/// gem's fixed atom resolves from, mirroring <c>ConsumableSeed</c>'s identical three-field shape
/// (`ConsumableCorpus.cs`) — the same seed-contract discipline applies here: this module parses and
/// resolves, it does not invent a magnitude the corpus never authored.
/// </summary>
public sealed record GemSeed(
    string ContainerId,
    string NameKey,
    string Name,
    string Family,
    string? Element,
    string PowerBand,
    string? AffinityElement,
    IReadOnlyList<string> Tags);

public sealed class GemCorpusRejection : Exception
{
    public GemCorpusRejection(string detail) : base(detail) { }
}

/// <summary>
/// <c>gk-data/packs/fusion/data/seed/items/gems/*.json</c>, parsed. Pure — the caller supplies the JSON text; Core never
/// opens a file, matching every other corpus reader in this program (`ConsumableCorpus`, `AffixFamilyFile`).
/// </summary>
public static class GemCorpus
{
    public static IReadOnlyList<GemSeed> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new GemCorpusRejection("empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new GemCorpusRejection($"not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) ||
                entries.ValueKind != JsonValueKind.Array)
                throw new GemCorpusRejection("no 'entries' array");

            var result = new List<GemSeed>();
            foreach (var e in entries.EnumerateArray()) result.Add(ReadEntry(e));
            return result;
        }
    }

    static GemSeed ReadEntry(JsonElement e)
    {
        var id = Str(e, "id");
        if (!id.StartsWith("gem.", StringComparison.Ordinal))
            throw new GemCorpusRejection($"gem entry '{id}' does not carry the 'gem.' prefix entry-shapes.md §1 fixes");

        var tags = new List<string>();
        if (e.TryGetProperty("tags", out var tEl) && tEl.ValueKind == JsonValueKind.Array)
            foreach (var t in tEl.EnumerateArray())
                if (t.ValueKind == JsonValueKind.String) tags.Add(t.GetString()!);

        return new GemSeed(
            id, Str(e, "nameKey"), Str(e, "name"), Str(e, "family"),
            OptStr(e, "element"), Str(e, "powerBand"), OptStr(e, "affinityElement"), tags);
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new GemCorpusRejection($"missing or non-string '{key}'");
        return el.GetString()!;
    }

    static string? OptStr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;
}
