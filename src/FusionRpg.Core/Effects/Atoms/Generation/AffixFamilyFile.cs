using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// Pure parser for one authored <c>affix-family</c> seed file
/// (<c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c>) — E43's generator INPUT, never imported by
/// <see cref="AtomSeedFile"/> itself (spec-family-expand.md §3.1, decided 2026-09-03: "the definitions
/// do not move, and the importer never sees them" — only this generator reads the folder).
///
/// <para>Reads the six columns <see cref="FamilyEntryInput"/> carries — five identity/formula columns
/// plus the family's own authored <c>tags</c> (`family-tags-closure`, D28, 2026-09-07: item's own
/// content, read here only so E43 can carry it through, never re-authored). Everything else on an
/// entry (roles, frames, nameWords, notes, variants...) is still the item program's own authored
/// surface and out of scope here — reconcile and expand only, never re-author (spec §4).</para>
/// </summary>
public static class AffixFamilyFile
{
    public static IReadOnlyList<FamilyEntryInput> Read(string sourceFileName, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out var entriesEl)
            || entriesEl.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{sourceFileName}: expected an object with an 'entries' array");

        var list = new List<FamilyEntryInput>();
        foreach (var e in entriesEl.EnumerateArray())
        {
            var id = Str(e, "id");
            if (id.Length == 0)
                throw new FormatException($"{sourceFileName}: an entry has no 'id'");

            var name = Str(e, "name");
            var kindId = Str(e, "kindId");
            var powerBand = Str(e, "powerBand");

            var channel = "";
            string? op = null;
            if (e.TryGetProperty("params", out var pars) && pars.ValueKind == JsonValueKind.Object)
            {
                channel = Str(pars, "channel");
                if (pars.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String)
                    op = opEl.GetString();
            }

            var tags = ReadTags(sourceFileName, id, e);

            list.Add(new FamilyEntryInput(id, name, kindId, channel, op, powerBand, sourceFileName, tags));
        }

        return list;
    }

    static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    /// <summary>The entry's own <c>"tags"</c> array, if present — refused (not silently dropped) if a
    /// caller authors a non-string element, matching this file's own refusal style for a missing
    /// <c>id</c>. Absent entirely (no <c>tags</c> key) is legal and yields empty, not a refusal — most
    /// of the real corpus's history shipped with no tags array at all.</summary>
    static IReadOnlyList<string> ReadTags(string sourceFileName, string id, JsonElement e)
    {
        if (!e.TryGetProperty("tags", out var tagsEl)) return Array.Empty<string>();
        if (tagsEl.ValueKind != JsonValueKind.Array)
            throw new FormatException($"{sourceFileName}: entry '{id}' has a 'tags' that is not an array");

        var tags = new List<string>();
        foreach (var t in tagsEl.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.String)
                throw new FormatException($"{sourceFileName}: entry '{id}' has a non-string tag");
            tags.Add(t.GetString()!);
        }
        return tags;
    }
}
