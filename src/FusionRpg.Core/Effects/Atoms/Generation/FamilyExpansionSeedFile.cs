using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// The ONE canonical serializer for E43's generated atom-seed files (spec-family-expand.md §3.2).
///
/// <para><b>Why this exists as shared code rather than a local helper in the tool.</b> The generator
/// writes these files and the test suite verifies them; when each side owned its own serializer they
/// could — and did — disagree. Three committed files drifted from their sources for a full commit
/// cycle (tasks/passive-tree-repair-plan.md P1.1): two carried a stale authored <c>name</c> after a
/// family rename, and one differed on a single line ending. The id-only comparison the test used
/// could see neither, because a rename keeps every id and a newline keeps every byte except one.</para>
///
/// <para><b>The line-ending contract.</b> <c>JsonSerializer</c>'s <c>WriteIndented</c> output
/// separates lines with <c>Environment.NewLine</c> — CRLF on Windows, LF elsewhere — so the same
/// content produced a different byte stream per platform. Normalising to LF here (and comparing
/// through <see cref="Canonicalize"/> in <c>--check</c>) makes the committed tree reproducible on any
/// machine, matching <c>seedsmith...plan.emit.canonical_json_bytes</c>'s contract for the committed
/// passive-tree plan: LF, UTF-8 with no BOM, trailing newline.</para>
///
/// <para><b>Writing bytes, never text.</b> Callers must use <see cref="WriteCanonical"/> (or
/// <see cref="Canonicalize"/>) rather than a text API: a default <c>File.WriteAllText</c> /
/// <c>Path.write_text</c> re-translates <c>\n</c> back to the platform separator on write, which is
/// exactly how the mixed-ending file appeared in the first place. A working tree is also subject to
/// <c>core.autocrlf</c>, so a reader must canonicalize BEFORE comparing — never assert the absence of
/// CRLF, which is an environment property, not a content one.</para>
/// </summary>
public static class FamilyExpansionSeedFile
{
    /// <summary>Canonical form of any text this pipeline reads or writes: LF only.</summary>
    public static string Canonicalize(string text) => text.Replace("\r\n", "\n");

    /// <summary>Serialise one source file's rows to the committed seed-file text: entries sorted by
    /// atom id (deterministic regardless of grouping order), LF endings, UTF-8-friendly, and a single
    /// trailing newline. The <c>when</c> key is emitted ONLY for rows that carry one (status rows
    /// with a grant chance today) — the importer defaults absent <c>when</c> to <c>{}</c>
    /// (<c>AtomSeedFile</c>), so existing rows are byte-untouched by this arm.</summary>
    public static string ToCanonicalJson(IReadOnlyList<AtomRow> rows)
    {
        var sorted = rows.OrderBy(r => r.AtomId, StringComparer.Ordinal);
        var entries = new JsonArray();
        foreach (var row in sorted)
        {
            var entry = new JsonObject
            {
                ["family"] = row.FamilyId,
                ["tier"] = row.Tier,
                ["kind"] = row.KindId,
                ["name"] = row.Name,
                ["params"] = JsonNode.Parse(row.ParamsJson),
                ["tags"] = JsonNode.Parse(row.TagsJson),
            };
            if (row.WhenJson != "{}")
                entry["when"] = JsonNode.Parse(row.WhenJson);
            entries.Add(entry);
        }

        var file = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["kind"] = "atom",
            ["entries"] = entries,
        };

        return Canonicalize(file.ToJsonString(new JsonSerializerOptions { WriteIndented = true })) + "\n";
    }

    /// <summary>Write canonical bytes directly. Never <c>File.WriteAllText</c> — see the class note.</summary>
    public static void WriteCanonical(string path, string canonicalJson) =>
        File.WriteAllBytes(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(canonicalJson));
}
