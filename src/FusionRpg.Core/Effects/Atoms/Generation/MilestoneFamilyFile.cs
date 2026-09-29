using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// Pure parser for the enhancement-milestone seed file
/// (<c>gk-data/packs/fusion/data/seed/items/enhancement-milestones/milestones.json</c>) — species-gear-chain T12. Same six
/// columns <see cref="FamilyEntryInput"/> carries as <see cref="AffixFamilyFile"/>, with the one
/// difference the milestone shape forces: the family's identity is <c>runtimeFamily</c> (the atom
/// stem the runtime reads, e.g. <c>atom.enhance-vigor</c>), NOT <c>id</c> (the ledger key, e.g.
/// <c>enh.001</c>). Reading <c>id</c> as the family would emit rows under a stem nothing references —
/// the exact fork this reader exists to prevent. A missing or empty <c>runtimeFamily</c> is a
/// refusal naming the entry, never a silent fall back to <c>id</c>.
/// </summary>
public static class MilestoneFamilyFile
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
            if (!e.TryGetProperty("runtimeFamily", out var rf) || rf.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(rf.GetString()))
                throw new FormatException(
                    $"{sourceFileName}: entry '{id}' has no 'runtimeFamily' — the atom stem is the family's " +
                    "identity here, never the ledger id");
            var familyId = rf.GetString()!;

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

            var tags = new List<string>();
            if (e.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tagsEl.EnumerateArray())
                {
                    if (t.ValueKind != JsonValueKind.String)
                        throw new FormatException($"{sourceFileName}: entry '{id}' has a non-string tag");
                    tags.Add(t.GetString()!);
                }
            }

            list.Add(new FamilyEntryInput(familyId, name, kindId, channel, op, powerBand, sourceFileName, tags));
        }

        return list;
    }

    static string Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
}
