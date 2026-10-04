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
    /// <summary>
    /// The keys <see cref="ReadOne"/> reads through <see cref="Str"/>, in THIS reader's own
    /// evaluation order — and the reader reads them FROM this list, so there is one declaration of
    /// the set rather than a constant beside the code it is supposed to describe.
    ///
    /// <para>Order is load-bearing, not cosmetic: <see cref="ReadOne"/> is fail-fast (it raises at
    /// the first bad field and never reaches a later one), so which field's message a caller sees
    /// depends on this sequence. <c>gameTypeId</c> is NOT here — it is the separate integer guard
    /// (<see cref="TryGameTypeId"/>) and is evaluated between <c>side</c> and <c>elementPrimary</c>;
    /// that interleaving is recorded here rather than left to be re-derived.</para>
    ///
    /// <para><see cref="AnchorRowContract"/> enumerates this same constant instead of restating the
    /// eleven names, which is the only reason it can be true of this reader rather than of the day
    /// somebody transcribed it.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> StrFields = new[]
    {
        "speciesId", "aptitudeSecondary", "elementSecondary",
        "rarity", "aptitudePrimary", "attackTempo", "reach", "side",
        // <- `gameTypeId` raises between `side` and `elementPrimary`. It is an int, not a Str.
        "elementPrimary", "deployMode", "targetPreference",
    };

    public static IReadOnlyList<AnchorRow> ReadAll(string json)
    {
        using var doc = RequireArrayDocument(json);
        var rows = new List<AnchorRow>();
        foreach (var el in doc.RootElement.EnumerateArray()) rows.Add(ReadOne(el));
        return rows;
    }

    /// <summary>
    /// <see cref="ReadAll"/>'s two FILE-level guards on their own: a document that is not valid
    /// JSON, and one whose root is not an array. Both produce no row at all, which is why neither
    /// can be reached from a per-entry check and why restating them in prose leaves them unchecked.
    ///
    /// <para>Separated from <see cref="ReadAll"/> so there is one implementation rather than two:
    /// <see cref="ReadAll"/> calls THIS, and so does <see cref="AnchorRowContract"/>, which is the
    /// only caller that wants them without the rows.</para>
    /// </summary>
    public static JsonDocument RequireArrayDocument(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new AnchorRowRejection($"anchor file: not valid JSON — {ex.Message}");
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            doc.Dispose();
            throw new AnchorRowRejection("anchor file: expected a top-level array");
        }

        return doc;
    }

    public static AnchorRow ReadOne(JsonElement el)
    {
        // Every `Str`-guarded key is checked up front, in `StrFields`' own order, so the first
        // refusal is the same field it has always been — and so the contract enumerates one list
        // rather than a second copy of eleven names that could drift from this one.
        foreach (var key in StrFields) Str(el, key);
        // ... and `gameTypeId` in its own slot between `side` and `elementPrimary`, for the same
        // reason: the order this method raises in is the order `StrFields` documents.
        if (!TryGameTypeId(el, out var gameTypeId))
            throw new AnchorRowRejection("anchor: missing or non-integer 'gameTypeId'");

        return new AnchorRow(
            SpeciesId: Text(el, "speciesId"),
            Rarity: Text(el, "rarity"),
            ThreatBand: el.TryGetProperty("threatBand", out var tb) && tb.ValueKind == JsonValueKind.String
                ? tb.GetString() : null,
            AptitudePrimary: Text(el, "aptitudePrimary"),
            AptitudeSecondary: Optional(el, "aptitudeSecondary"),
            Pure: el.TryGetProperty("pure", out var p) && p.ValueKind == JsonValueKind.True,
            AttackTempo: Text(el, "attackTempo"),
            Reach: Text(el, "reach"),
            Variants: StrArray(el, "variants"),
            Side: Text(el, "side"),
            GameTypeId: gameTypeId,
            ElementPrimary: Text(el, "elementPrimary"),
            ElementSecondary: Optional(el, "elementSecondary"),
            DeployMode: Text(el, "deployMode"),
            Acquisition: StrArray(el, "acquisition"),
            Traits: StrArray(el, "traits"),
            TargetPreference: Text(el, "targetPreference"),
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

    /// <summary>The reader's own `Str` guard, raised. Public so `AnchorRowContract` reports the
    /// consumer's message instead of a paraphrase of it.</summary>
    public static string Str(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new AnchorRowRejection($"anchor: missing or non-string '{key}'");

    /// <summary>The same test <see cref="Str"/> makes, without the raise — the contract's reason for
    /// being able to report every bad field in one pass where the consumer stops at the first.</summary>
    public static bool TryStr(JsonElement el, string key, out string value)
    {
        if (el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString()!;
            return true;
        }
        value = "";
        return false;
    }

    /// <summary><c>gameTypeId</c>'s own guard. Found by the contract's differential test, 2026-10-04:
    /// <c>JsonElement.TryGetInt32</c> does not return false on a non-numeric element — it THROWS
    /// <see cref="InvalidOperationException"/> — so <c>TryGetProperty &amp;&amp; TryGetInt32</c> raised that
    /// instead of the <see cref="AnchorRowRejection"/> below for every present-but-non-integer value
    /// (<c>"20"</c>, <c>true</c>, <c>20.5</c>). The guard's own message promised
    /// <c>missing or non-integer</c> and could only ever deliver <c>missing</c>.
    ///
    /// <para>The <c>ValueKind == Number</c> test below is what makes the message true. It is a fix,
    /// not a loosening: an unhandled <see cref="InvalidOperationException"/> out of a batch loader is
    /// a crash the caller cannot attribute to an anchor, and every value this now refuses was already
    /// refused — just unrecognisably. <see cref="AnchorRowContract"/> needs the non-raising form as
    /// well, so there is one test here rather than two that can disagree.</para></summary>
    public static bool TryGameTypeId(JsonElement el, out int gameTypeId)
    {
        if (el.TryGetProperty("gameTypeId", out var g) && g.ValueKind == JsonValueKind.Number &&
            g.TryGetInt32(out var gi))
        {
            gameTypeId = gi;
            return true;
        }
        gameTypeId = 0;
        return false;
    }

    /// <summary>A key already cleared by the <see cref="StrFields"/> sweep, so it cannot be absent
    /// and non-null here. Reading it through <see cref="Str"/> again would be a second raise that
    /// can never fire, which is exactly the kind of guard a coverage count inflates itself with.</summary>
    static string Text(JsonElement el, string key) => Str(el, key);

    /// <summary>One of the two optional-secondary fields: the literal <c>"none"</c> sentinel, matched
    /// case-INSENSITIVELY and UNTRIMMED, maps to null. Kept as its own method because the reader's
    /// mapping is itself a guard — <c>" none"</c> is not the sentinel and must survive as a value.</summary>
    static string? Optional(JsonElement el, string key)
    {
        var value = Str(el, key);
        return string.Equals(value, "none", StringComparison.OrdinalIgnoreCase) ? null : value;
    }

    static string? ThreatConfidence(JsonElement el)
    {
        if (el.TryGetProperty("_provenance", out var prov) && prov.ValueKind == JsonValueKind.Object &&
            prov.TryGetProperty("confidence", out var conf) && conf.ValueKind == JsonValueKind.Object &&
            conf.TryGetProperty("threatBand", out var tb) && tb.ValueKind == JsonValueKind.String)
            return tb.GetString();
        return null;
    }

    static IReadOnlyList<string> StrArray(JsonElement el, string key) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString() ?? "").ToArray()
            : Array.Empty<string>();
}
