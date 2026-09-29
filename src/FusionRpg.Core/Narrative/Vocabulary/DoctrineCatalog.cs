using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FusionRpg.Core.Narrative.Vocabulary;

/// <summary>`spec-narrative-vocabulary.md` §1, map row 24: one doctrine the antagonist may study. The list
/// is `counter-doctrine`'s closed reviewed vocabulary, not this module's — this module ships the registry
/// FILE SHAPE and the reader, and the file is empty at first ship.</summary>
public sealed record DoctrineDef(
    string Id, string Description, string Negative,
    IReadOnlyDictionary<string, int> SpeciesElementBiasMilli,
    IReadOnlyDictionary<string, int> OrderWeightMilli);

/// <summary>
/// `§1`: the doctrine catalog. It accepts an EMPTY list on purpose — `gk-data/packs/fusion/data/seed/narrative/_registry/doctrines.v1.json`
/// carries an empty `doctrines` object until `counter-doctrine` lands a reviewed row — so an empty file is a
/// legal, configured catalog, never a missing-registry rejection.
/// </summary>
public static class DoctrineCatalog
{
    const string File = "doctrines.v1.json";
    const string Key = "doctrines";

    /// <summary>
    /// The refusal a doctrine row earns for carrying any key that is not `description`, `negative` or one
    /// of the two closed effect families (`speciesElementBiasMilli.{element}`,
    /// `orderWeightMilli.{orderKind}`). It is the guard for R13's "never how strong": no doctrine key may
    /// name a magnitude, so `maxHp`/`atk`/`level`/`rarity` are refused BY RULE, not by convention
    /// (spec-counter-doctrine.md §3).
    /// </summary>
    public const string MagnitudeKeyRule = "doctrine.magnitude-key";

    const string ElementBiasKey = "speciesElementBiasMilli";
    const string OrderWeightKey = "orderWeightMilli";
    static readonly string[] RowKeys = { "description", "negative", ElementBiasKey, OrderWeightKey };

    /// <summary>The element ids a bias may name — the shipped element table's own six, so the doctrine
    /// keys against an existing closed vocabulary rather than a second spelling of it.</summary>
    public static IReadOnlyList<string> ElementKeys { get; } =
        FusionRpg.Core.Combat.Element.ElementTable.Shipped().Elements.Select(e => e.ElementId).ToList();

    /// <summary>The order kinds a weight may name — `WorldCommandKinds.All`, the closed vocabulary a
    /// policy can actually weight.</summary>
    public static IReadOnlyList<string> OrderKeys => FusionRpg.Core.World.Turn.WorldCommandKinds.All;

    static IReadOnlyList<DoctrineDef>? _all;
    static Dictionary<string, DoctrineDef>? _byId;

    public static IReadOnlyList<DoctrineDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static DoctrineDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown doctrine '{id}'.");

    public static void Configure(IReadOnlyList<DoctrineDef> rows)
    {
        Validate(rows);
        _all = rows;
        _byId = rows.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(DoctrineCatalog)}.Configure(...) has not run.");

    public static void Validate(IReadOnlyList<DoctrineDef> rows)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            NarrativeRegistryIds.RequireWireId(row.Id, "doctrine id", File);
            if (!seen.Add(row.Id))
                throw new NarrativeVocabularyRejection($"{File}: duplicate id '{row.Id}'.");
            if (string.IsNullOrWhiteSpace(row.Description) || string.IsNullOrWhiteSpace(row.Negative))
                throw new NarrativeVocabularyRejection($"{File}: '{row.Id}' must carry a description and a negative clause.");
        }
    }

    public static IReadOnlyList<DoctrineDef> Parse(string json)
    {
        var rows = new List<DoctrineDef>();
        foreach (var (id, row) in NarrativeRegistryJson.Rows(NarrativeRegistryJson.Vocabulary(json, Key, File), File))
        {
            // Not `RejectUnknownKeys`: a row key outside the four is the RULE's own refusal, so it must
            // carry `doctrine.magnitude-key` and say which key it saw.
            if (row.ValueKind != JsonValueKind.Object)
                throw new NarrativeVocabularyRejection($"{File}: '{id}' is not an object.");
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in row.EnumerateObject())
            {
                // A duplicate key is a content defect, and silently letting the last one win is how a
                // refused key would disappear.
                if (!seenKeys.Add(property.Name))
                    throw new NarrativeVocabularyRejection($"{File}: '{id}' carries key '{property.Name}' twice.");
                if (!RowKeys.Contains(property.Name, StringComparer.Ordinal))
                    throw new NarrativeVocabularyRejection(
                        $"{File}: '{id}' carries key '{property.Name}' — {MagnitudeKeyRule}: a doctrine may "
                        + $"carry only {string.Join(", ", RowKeys)}, and no key may name a magnitude.");
            }

            var text = NarrativeRegistryJson.Text(row, id, File);
            rows.Add(new DoctrineDef(
                id, text.Description, text.Negative,
                EffectMap(row, ElementBiasKey, id, ElementKeys),
                EffectMap(row, OrderWeightKey, id, OrderKeys)));
        }
        return rows;
    }

    /// <summary>One effect map: `{suffix: per-mille}` where every suffix is a member of the closed
    /// vocabulary the family keys against, and every value an integer.</summary>
    static IReadOnlyDictionary<string, int> EffectMap(
        JsonElement row, string family, string id, IReadOnlyList<string> allowed)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!row.TryGetProperty(family, out var element)) return map;
        if (element.ValueKind != JsonValueKind.Object)
            throw new NarrativeVocabularyRejection($"{File}: '{id}.{family}' must be an object.");

        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{id}.{family}.{property.Name}' is not in the closed vocabulary "
                    + $"({string.Join(", ", allowed)}) — {MagnitudeKeyRule}.");
            if (!map.TryAdd(property.Name, 0))
                throw new NarrativeVocabularyRejection($"{File}: '{id}.{family}.{property.Name}' appears twice.");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value))
                throw new NarrativeVocabularyRejection(
                    $"{File}: '{id}.{family}.{property.Name}' must be an integer per-mille value.");
            map[property.Name] = value;
        }

        return map;
    }
}
