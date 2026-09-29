using FusionRpg.Core.Narrative.Vocabulary;

namespace FusionRpg.Core.Narrative.Doctrine;

/// <summary>
/// counter-doctrine's own catalog (npc-story-events NR6.1, spec-counter-doctrine.md §3): the reviewed
/// vocabulary of doctrines the Rotwright may study, and the typed reads world-map's filed ask needs.
///
/// <para><b>One reader for the file, and this is not a second one.</b> The registry file's SHAPE and its
/// key closure belong to <see cref="DoctrineCatalog"/> (the vocabulary module ships the file and refuses
/// any key that is not `description`/`negative`/the two effect families, with
/// <see cref="DoctrineCatalog.MagnitudeKeyRule"/>). This catalog owns what that module cannot: which ids
/// are the REVIEWED set, and the per-element / per-order-kind reads a consumer wants instead of two
/// dictionaries.</para>
///
/// <para><b>Never how strong (R13).</b> Nothing here carries a magnitude: the only numbers are per-mille
/// biases and weights, and every consumer read returns one of those. A doctrine changes WHICH species and
/// orders the faction fields; magnitudes stay on `P(Θ)` and contests on `Θ`.</para>
/// </summary>
public static class DoctrinesCatalog
{
    /// <summary>
    /// The closed, reviewed vocabulary (spec-counter-doctrine.md §3): six `ward.{element}` — one per
    /// shipped element, each answering the element the ring says resists it — plus `siegecraft` (assault
    /// and siege orders against held ground) and `raiders` (raids on economic slots). Eight, and a new one
    /// is a reviewed change to the spec's table, not a data edit.
    /// </summary>
    public static readonly IReadOnlyList<string> ReviewedIds = new[]
    {
        "ward.fire", "ward.ice", "ward.air", "ward.earth", "ward.light", "ward.dark",
        "siegecraft", "raiders"
    };

    static IReadOnlyList<DoctrineDef>? _all;
    static Dictionary<string, DoctrineDef>? _byId;

    public static IReadOnlyList<DoctrineDef> All => _all ?? throw NotConfigured();

    public static bool IsKnown(string? id) => id != null && (_byId ?? throw NotConfigured()).ContainsKey(id);

    public static DoctrineDef Get(string id) =>
        (_byId ?? throw NotConfigured()).TryGetValue(id, out var row)
            ? row
            : throw new ArgumentException($"Unknown doctrine '{id}'.");

    /// <summary>Configures the reviewed set. Refuses a row outside <see cref="ReviewedIds"/>, a missing
    /// reviewed id, and any effect map that is empty on both families — a doctrine that changes nothing is
    /// not a doctrine.</summary>
    public static void Configure(IReadOnlyList<DoctrineDef> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var byId = new Dictionary<string, DoctrineDef>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!ReviewedIds.Contains(row.Id, StringComparer.Ordinal))
                throw new NarrativeVocabularyRejection(
                    $"doctrines.v1.json: '{row.Id}' is not one of the reviewed doctrines ({string.Join(", ", ReviewedIds)}).");
            if (!byId.TryAdd(row.Id, row))
                throw new NarrativeVocabularyRejection($"doctrines.v1.json: duplicate doctrine '{row.Id}'.");
            if (row.SpeciesElementBiasMilli.Count == 0 && row.OrderWeightMilli.Count == 0)
                throw new NarrativeVocabularyRejection(
                    $"doctrines.v1.json: '{row.Id}' carries no effect — a doctrine must bias species elements or order weights.");
        }

        foreach (var id in ReviewedIds)
            if (!byId.ContainsKey(id))
                throw new NarrativeVocabularyRejection($"doctrines.v1.json: the reviewed doctrine '{id}' is missing.");

        _all = rows;
        _byId = byId;
    }

    static InvalidOperationException NotConfigured() =>
        new($"{nameof(DoctrinesCatalog)}.Configure(...) has not run.");
}
