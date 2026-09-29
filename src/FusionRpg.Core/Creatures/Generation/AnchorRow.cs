using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// One classified anchor — the enum-only output of the LLM classification pipeline
/// (`gk-forge/tools/seedsmith/seedsmith/adapters/creatures/anchor/`), read back into C# for `species-generator`
/// (module 12). Carries every field `catalog-runtime` (module 14) needs to assemble a real
/// `CreatureSpeciesDef` alongside the fields `species-generator`'s own numeric derivation reads — the
/// anchor already has all of it (verified against the real anchor schema and the two real classified
/// anchors on disk, `pea.json`/`sunflower.json`, 2026-09-02); `_provenance` and `resourceProfile` are
/// the only real anchor fields still deliberately unparsed here, because nothing downstream reads
/// them yet.
/// </summary>
/// <param name="ThreatBand">Null when the real anchor has not been classified for this field yet —
/// verified against the two real classified anchors on disk today (`pea.json`, `sunflower.json`),
/// both of which genuinely omit it. `creature-threat.v1.json`'s own `inferredDefaultRung` is the
/// sanctioned fallback for exactly this case, not an invented default.</param>
/// <param name="AptitudeSecondary">Null when the anchor's own literal <c>"none"</c> sentinel is
/// present — the real anchor schema uses that string, not a JSON null, to mean "no secondary."</param>
/// <param name="ElementSecondary">Same <c>"none"</c>-sentinel convention as
/// <paramref name="AptitudeSecondary"/> — the real anchor schema uses one literal string convention
/// for every optional-secondary field, not a JSON null.</param>
/// <param name="Acquisition">The anchor's own raw flag-array strings (e.g. <c>["Summonable"]</c>) —
/// kept as strings here, parsed into the real <c>[Flags] CreatureAcquisition</c> enum by the caller
/// (mirrors how <see cref="Rarity"/> stays a raw string here and <c>SpeciesExpander</c> parses it),
/// so this reader stays a pure JSON-shape mirror with no enum-parsing failure mode of its own.</param>
/// <param name="TargetPreference">party-dungeon `encounter-generator` (D2.1) — required, unlike
/// <see cref="ThreatBand"/>: a corpus-wide scan (2026-09-06, all 841 real anchors under
/// `gk-data/packs/fusion/data/seed/creatures/species/`) found zero missing, against six real values (`frontline 221 ·
/// backline 344 · swarm 193 · elite 17 · structure 50 · indiscriminate 16`) — so this stays a plain
/// required string, matching <see cref="Reach"/>/<see cref="AttackTempo"/>'s own treatment, not
/// <see cref="ThreatBand"/>'s nullable one.</param>
/// <param name="SpeciesKind">R-CS3/R-CS4's own closed mark, read verbatim from the seed anchor's
/// top-level <c>speciesKind</c> (<c>creature</c> | <c>excluded</c>). Kept as the raw string here for
/// the same reason <paramref name="Acquisition"/> is — this reader mirrors the JSON shape and leaves
/// interpretation to the one declaring site. Null when the anchor omits it (pre-mark anchors), which
/// callers read as <c>creature</c>.</param>
/// <param name="ThreatBandConfidence">The anchor's own <c>_provenance.confidence.threatBand</c>
/// stamp when present — <c>scored</c> / <c>default</c> (threat-band-fill T3), <c>high</c> /
/// <c>split</c> (a real classification vote), or the older <c>deterministic-fallback</c> stamp
/// pre-fill fixes wrote. Null when the anchor carries no stamp (pre-fill corpus). Read-only
/// here: the quality report's provenance split reads it, nothing downstream decides on it.</param>
/// <param name="Rank">The anchor's own DERIVED <c>rank</c> (spec-species-rank.md §1): the
/// <c>creature-rank.v1.json</c> cell for this species' resolved <c>(threatBand, rarity)</c> pair.
/// Null when the anchor omitted the key (pre-rank corpus) OR wrote the pipeline's literal
/// <c>"unresolved"</c> for it — §1.7's sentinel mapping, because C# models the same honest gap as
/// an absent value, never a default. Nullability is the ONE thing this field borrows from
/// <see cref="ThreatBand"/>: <see cref="Rarity"/> stays non-nullable and still throws on missing.</param>
public sealed record AnchorRow(
    string SpeciesId, string Rarity, string? ThreatBand,
    string AptitudePrimary, string? AptitudeSecondary, bool Pure,
    string AttackTempo, string Reach, IReadOnlyList<string> Variants,
    string Side, int GameTypeId, string ElementPrimary, string? ElementSecondary,
    string DeployMode, IReadOnlyList<string> Acquisition, IReadOnlyList<string> Traits,
    string TargetPreference, string? ThreatBandConfidence = null,
    string? SpeciesKind = null, string? Rank = null);

public sealed class AnchorRowRejection : Exception
{
    public AnchorRowRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O — reads the real classified-anchor JSON shape (a top-level
/// array of anchor objects, `gk-data/packs/fusion/data/seed/creatures/species/**.json`).</summary>
public static class AnchorRowReader
{
    public static IReadOnlyList<AnchorRow> ReadAll(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new AnchorRowRejection($"anchor file: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new AnchorRowRejection("anchor file: expected a top-level array");

            var rows = new List<AnchorRow>();
            foreach (var el in doc.RootElement.EnumerateArray()) rows.Add(ReadOne(el));
            return rows;
        }
    }

    static AnchorRow ReadOne(JsonElement el)
    {
        var speciesId = Str(el, "speciesId");
        var aptSecondary = Str(el, "aptitudeSecondary");
        var elSecondary = Str(el, "elementSecondary");
        return new AnchorRow(
            SpeciesId: speciesId,
            Rarity: Str(el, "rarity"),
            ThreatBand: el.TryGetProperty("threatBand", out var tb) && tb.ValueKind == JsonValueKind.String
                ? tb.GetString() : null,
            AptitudePrimary: Str(el, "aptitudePrimary"),
            AptitudeSecondary: string.Equals(aptSecondary, "none", StringComparison.OrdinalIgnoreCase) ? null : aptSecondary,
            Pure: el.TryGetProperty("pure", out var p) && p.ValueKind == JsonValueKind.True,
            AttackTempo: Str(el, "attackTempo"),
            Reach: Str(el, "reach"),
            Variants: StrArray(el, "variants"),
            Side: Str(el, "side"),
            GameTypeId: el.TryGetProperty("gameTypeId", out var g) && g.TryGetInt32(out var gi)
                ? gi : throw new AnchorRowRejection("anchor: missing or non-integer 'gameTypeId'"),
            ElementPrimary: Str(el, "elementPrimary"),
            ElementSecondary: string.Equals(elSecondary, "none", StringComparison.OrdinalIgnoreCase) ? null : elSecondary,
            DeployMode: Str(el, "deployMode"),
            Acquisition: StrArray(el, "acquisition"),
            Traits: StrArray(el, "traits"),
            TargetPreference: Str(el, "targetPreference"),
            ThreatBandConfidence: ThreatConfidence(el),
            // R-CS3/R-CS4's mark. Optional on purpose: the whole corpus carries it today, but a
            // pre-mark anchor must read as `creature` rather than be refused for a missing key.
            SpeciesKind: el.TryGetProperty("speciesKind", out var sk) && sk.ValueKind == JsonValueKind.String
                ? sk.GetString() : null,
            // spec-species-rank.md §1.7: the pipeline writes the literal "unresolved" for a rank it
            // SKIPPED (an unresolved threatBand/rarity) and C# models that gap as null. An absent
            // key says the same thing — a pre-rank anchor — so both read as null, never a default.
            Rank: el.TryGetProperty("rank", out var rk) && rk.ValueKind == JsonValueKind.String &&
                  !string.Equals(rk.GetString(), "unresolved", StringComparison.OrdinalIgnoreCase)
                ? rk.GetString() : null);
    }

    static string? ThreatConfidence(JsonElement el)
    {
        if (el.TryGetProperty("_provenance", out var prov) && prov.ValueKind == JsonValueKind.Object &&
            prov.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Object &&
            conf.TryGetProperty("threatBand", out var tb) && tb.ValueKind == JsonValueKind.String)
            return tb.GetString();
        return null;
    }

    static string Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new AnchorRowRejection($"anchor: missing or non-string '{key}'");

    static IReadOnlyList<string> StrArray(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : Array.Empty<string>();
}
