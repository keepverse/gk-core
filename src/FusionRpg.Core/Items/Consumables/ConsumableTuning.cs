using System.Text.Json;

namespace FusionRpg.Core.Items.Consumables;

public sealed class ConsumableTuningRejection : Exception
{
    public ConsumableTuningRejection(string message) : base(message) { }
}

/// <summary>
/// Pure parser over <c>gk-core/data/tuning/consumables.v1.json</c> — no file I/O (tunables-ssot.md §7.2:
/// "Core never reads a file. Hosts load and inject"), matching <see cref="Uniques.UniqueTuning"/>,
/// <see cref="Sockets.SocketTuning"/>, <see cref="Mutation.EnhancementTuning"/> and
/// <see cref="Materials.MaterialTuning"/>.
///
/// <para><b>No key has a default.</b> A missing key throws at load rather than resolving to a silently
/// invented grade map or authoring ceiling.</para>
///
/// <para><b>Four structural invariants are checked at parse time.</b> (1) <c>gradeTierMap</c> must be a
/// bijection onto <see cref="ConsumableLimits.MinGrade"/>..<see cref="ConsumableLimits.MaxGrade"/> —
/// a map with a hole or a duplicate would grade two power bands the same and make §5.2's
/// band-consistency check pass on a row it should refuse. (2) <c>classesAuthored</c> and
/// (3) <c>contextsAuthored</c> must be non-empty subsets of their closed vocabularies — an empty list
/// authors nothing and reads as a disabled feature, and an unknown member is a typo that would
/// silently authorize nothing. (4) <c>authoringCeilingPerMille</c> is a bounded ratio inside 1..1000.
/// </para>
///
/// <para>⛔ <b>There is deliberately no carry limit in this file.</b> D37 withdrew §10.1's proposed
/// <c>N</c>: the limit is the equipped <c>girdle</c>'s own <c>consumableSlots</c>, which is content on
/// a base type. A parser that accepted an <c>N</c> here would let a balance pass re-impose the global
/// ceiling D37 removed, so the key is not read and not defaulted — it simply does not exist.</para>
/// </summary>
public sealed class ConsumableTuning
{
    ConsumableTuning(
        IReadOnlyList<ConsumableClass> classesAuthored,
        IReadOnlyList<UseContext> contextsAuthored,
        IReadOnlyDictionary<string, int> gradeTierMap,
        int authoringCeilingPerMille,
        int draughtBindingPriority)
    {
        ClassesAuthored = classesAuthored;
        ContextsAuthored = contextsAuthored;
        GradeTierMap = gradeTierMap;
        AuthoringCeilingPerMille = authoringCeilingPerMille;
        DraughtBindingPriority = draughtBindingPriority;
    }

    /// <summary>ssot-consumables.md §3.1's v1 column — which of the six classes v1 authors.</summary>
    public IReadOnlyList<ConsumableClass> ClassesAuthored { get; }

    /// <summary>§5.2 — which of the four contexts v1 authors.</summary>
    public IReadOnlyList<UseContext> ContextsAuthored { get; }

    /// <summary>`bands.v1.json`'s frozen <c>powerBand.tierMap</c>, mirrored. Core never reads a file.</summary>
    public IReadOnlyDictionary<string, int> GradeTierMap { get; }

    /// <summary>§4.4's ≤10% authoring ceiling, in per-mille. Bounds a REPORT, never an import.</summary>
    public int AuthoringCeilingPerMille { get; }

    /// <summary>§4.3 / §9 item 10 — the run-start binding priority, mirroring charms exactly.</summary>
    public int DraughtBindingPriority { get; }

    public bool Authors(ConsumableClass c) => ClassesAuthored.Contains(c);

    public bool Authors(UseContext u) => ContextsAuthored.Contains(u);

    /// <summary>
    /// The grade a <c>powerBand</c> resolves to. ssot-consumables.md §5.2's "grade MUST equal the tier
    /// of every core atom" is checkable against a seed that authors a band and never a tier only
    /// because this map exists.
    /// </summary>
    public bool TryGradeFor(string? powerBand, out int grade)
    {
        grade = 0;
        return powerBand is not null && GradeTierMap.TryGetValue(powerBand, out grade);
    }

    public static ConsumableTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ConsumableTuningRejection("consumables tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ConsumableTuningRejection($"consumables tuning: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new ConsumableTuningRejection("consumables tuning: root must be an object");

            var classes = new List<ConsumableClass>();
            foreach (var id in StringArray(root, "classesAuthored"))
            {
                if (!ConsumableClasses.TryParse(id, out var c))
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: classesAuthored names '{id}', which is not one of the six " +
                        "closed class ids — a class the enum does not carry authorizes nothing and reads " +
                        "as coverage");
                if (classes.Contains(c))
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: classesAuthored names '{id}' twice");
                classes.Add(c);
            }
            if (classes.Count == 0)
                throw new ConsumableTuningRejection(
                    "consumables tuning: classesAuthored is empty, which authors no consumable at all — " +
                    "that is a disabled feature, not a balance setting");

            var contexts = new List<UseContext>();
            foreach (var id in StringArray(root, "contextsAuthored"))
            {
                if (!UseContexts.TryParse(id, out var u))
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: contextsAuthored names '{id}', which is not one of the four " +
                        "closed use contexts");
                if (contexts.Contains(u))
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: contextsAuthored names '{id}' twice");
                contexts.Add(u);
            }
            if (contexts.Count == 0)
                throw new ConsumableTuningRejection(
                    "consumables tuning: contextsAuthored is empty, so no consumable could be used " +
                    "anywhere");

            if (!root.TryGetProperty("gradeTierMap", out var mapEl) || mapEl.ValueKind != JsonValueKind.Object)
                throw new ConsumableTuningRejection("consumables tuning: no 'gradeTierMap' object");

            var map = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var p in mapEl.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Number)
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: gradeTierMap['{p.Name}'] is not a number");
                map[p.Name] = p.Value.GetInt32();
            }

            var expected = Enumerable.Range(ConsumableLimits.MinGrade,
                ConsumableLimits.MaxGrade - ConsumableLimits.MinGrade + 1).ToList();
            var got = map.Values.OrderBy(v => v).ToList();
            if (!expected.SequenceEqual(got))
                throw new ConsumableTuningRejection(
                    $"consumables tuning: gradeTierMap must be a bijection onto grades " +
                    $"{ConsumableLimits.MinGrade}..{ConsumableLimits.MaxGrade}; it maps " +
                    $"[{string.Join(",", got)}]. A hole or a duplicate would grade two power bands the " +
                    "same and make §5.2's band-consistency check pass on a row it should refuse");

            var ceiling = Int(root, "authoringCeilingPerMille");
            // BOUNDED RATIO (per-mille, AGENTS.md's named exemption), not a progression ceiling: it
            // bounds a REPORT about how much of a geared actor's contribution a consumable may author,
            // never how much a player may earn.
            if (ceiling is < 1 or > 1000)
                throw new ConsumableTuningRejection(
                    $"consumables tuning: authoringCeilingPerMille {ceiling} is outside 1..1000‰");

            var priority = Int(root, "draughtBindingPriority");

            // ⛔ The withdrawn key, refused BY NAME rather than ignored. D37 replaced §10.1's global N
            // with the equipped girdle's own `consumableSlots`; a tuning that still carries one would
            // silently do nothing here, and "the number I set had no effect" is the worst failure a
            // balance file can have.
            foreach (var withdrawn in new[] { "carryLimit", "maxManifestEntries", "n", "N" })
                if (root.TryGetProperty(withdrawn, out _))
                    throw new ConsumableTuningRejection(
                        $"consumables tuning: '{withdrawn}' is withdrawn by D37 — the carry limit is the " +
                        "equipped girdle's own consumableSlots (content on a base type, module 6), not a " +
                        "config row. Remove the key; setting it here would do nothing");

            return new ConsumableTuning(classes, contexts, map, ceiling, priority);
        }
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new ConsumableTuningRejection($"consumables tuning: missing or non-numeric '{key}'");
        return el.GetInt32();
    }

    static IReadOnlyList<string> StringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new ConsumableTuningRejection($"consumables tuning: no '{key}' array");
        var list = new List<string>();
        foreach (var item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new ConsumableTuningRejection($"consumables tuning: '{key}' must hold id strings");
            list.Add(item.GetString()!);
        }
        return list;
    }
}
