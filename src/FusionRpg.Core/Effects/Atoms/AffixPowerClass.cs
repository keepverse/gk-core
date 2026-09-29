using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The L0 power class of an affix — how strong it is <b>as an idea</b>, independent of its tier or
/// its numbers (<c>effect-pipeline</c> module 11, <c>spec-affix-power-class.md</c>). Five classes,
/// append-only, consecutive ordinals.
///
/// <para><b>Identity, never a number and never a rate.</b> That is seedsmith's P1 without amendment
/// — <i>the LLM writes identity, deterministic code writes magnitude</i> — and the owner restated it
/// for this module: <i>"the LLM will resolve by closed enum; our deterministic engine will
/// distribute and resolve atom effect rate by the enum."</i> A weight, rate, probability or
/// magnitude on this enum would be a balance number living in the wrong place; the
/// <c>(powerClass × channel) → weight</c> policy is module 12's and lives in <c>gk-core/data/tuning/</c>.</para>
///
/// <para><b>The ordinal is structural metadata, not a magnitude.</b> It is what
/// <c>powerClassOf(affixId) := MAX over the affix's refs of familyPowerClass(ref)</c> compares by —
/// a bundle is as strong as its strongest member, because averaging would let one <c>pinnacle</c>
/// atom launder itself into a cheaper pool behind two <c>filler</c> ones. That is why the ordinals
/// are CONSECUTIVE and never renumbered, and why the spec's own 2026-09-03 correction stands: the
/// earlier "spaced by 10 is the house convention" claim was an invented precedent (no roster here
/// spaces — <c>ElementRow</c> 0–5, <c>Aptitude</c> 0–11, <c>CreatureRarity</c> 0–9).</para>
///
/// <para><b>No id here is a rarity-rung id.</b> Power class and rarity are different axes and must
/// never be confusable; "one word, four meanings" is a named defect in <c>enrichment-contract.md</c>
/// §1, and <c>ssot-rarity.md</c> §4.3 already had to correct a case where <c>unique</c> and
/// <c>set</c> were mistaken for rungs. <c>AffixPowerClassTests</c> proves the disjointness against
/// the ladder the code itself derives its rung ids from.</para>
///
/// <para><b>Adding a sixth is Ask first</b> (spec "Boundaries"), exactly as widening the 12 atom
/// kinds or the triggers is.</para>
/// </summary>
public enum AffixPowerClass
{
    /// <summary>Ordinal 0 — pads a pool. Nobody builds around it.</summary>
    Filler = 0,

    /// <summary>Ordinal 1 — a build takes it if offered.</summary>
    Notable = 1,

    /// <summary>Ordinal 2 — shapes a build's direction.</summary>
    Potent = 2,

    /// <summary>Ordinal 3 — a build is <i>about</i> this effect.</summary>
    Defining = 3,

    /// <summary>Ordinal 4 — top-shelf. The thing channels exist to gate.</summary>
    Pinnacle = 4,
}

/// <summary>
/// A power-class id the vocabulary does not carry, or a registry document that disagrees with the
/// mirror. Thrown, never defaulted — see <see cref="AffixPowerClassIds"/>.
/// </summary>
public sealed class AffixPowerClassRejection : Exception
{
    public AffixPowerClassRejection(string message) : base(message) { }
}

/// <summary>
/// The id grammar and the closed parse for <see cref="AffixPowerClass"/> — the C# mirror of
/// <c>gk-data/packs/fusion/data/seed/items/_registry/power-classes.v1.json</c>.
///
/// <para><b>One mirror, not two homes.</b> <c>AffixPowerClass</c> plus that registry file are the
/// only vocabulary homes in this slice: no second enum, no classifier-local id list, no channel
/// vocabulary and no allowlist. <see cref="AffixPowerClassRegistry.Parse"/> is what makes the pair
/// one fact rather than two — it refuses a document whose ids or ordinals are not exactly the
/// enum's, so the registry cannot drift into a vocabulary the code cannot name.</para>
///
/// <para><b>Closed, never defaulted.</b> There is no path here that answers
/// <see cref="AffixPowerClass.Filler"/> for a value it does not recognise — not
/// <see cref="TryParse"/>, not <see cref="Parse"/>. A silent default is the worst outcome the spec
/// can imagine: the strongest unclassified effect would land in the cheapest pool. An unclassified
/// family is a <c>blocked</c> answer in the classifier (module 11's next row), reported and flagged,
/// never guessed — and it is defaulted visibly to <see cref="AffixPowerClass.Notable"/> with an
/// <c>unclassified</c> flag, which is a different, deliberate, recorded decision made by the
/// consumer, not by this parser.</para>
/// </summary>
public static class AffixPowerClassIds
{
    /// <summary>Structural (<c>tunables-ssot.md</c> T2): a closed-vocabulary cardinality, not a
    /// balance dial. A sixth class moves this with the enum, the registry and the tests in one
    /// reviewed change — never this number alone.</summary>
    public const int Count = 5;

    /// <summary>Every id, in ordinal order. DERIVED from <see cref="Enum.GetValues{TEnum}"/> so a
    /// member added to the enum and forgotten everywhere else is a shape defect, not a silent
    /// omission; the id is the member name lowercased, which is the registry's
    /// <c>^[a-z]+$</c> id grammar.</summary>
    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(Enum.GetValues<AffixPowerClass>().Select(v => IdOf(v)).ToArray());

    /// <summary>The registry's id for a class. The inverse of <see cref="TryParse"/>.</summary>
    public static string IdOf(AffixPowerClass value) => value.ToString().ToLowerInvariant();

    /// <summary>Is this a value the vocabulary actually declares? A cast such as
    /// <c>(AffixPowerClass)99</c> is a bit pattern, not a class.</summary>
    public static bool IsDefined(AffixPowerClass value) => All.Contains(IdOf(value), StringComparer.Ordinal);

    /// <summary>
    /// Parse one id, or fail. Ordinal comparison: ids are bare lowercase words, so a mis-cased
    /// authored value is a reportable content error rather than a class that silently resolves —
    /// case folding would let a vocabulary grow a second spelling of itself.
    /// </summary>
    public static bool TryParse(string? id, out AffixPowerClass value)
    {
        value = default; // never Filler: a failed parse returns false and a caller must read that
        if (id is null) return false;

        foreach (var candidate in Enum.GetValues<AffixPowerClass>())
        {
            if (string.Equals(IdOf(candidate), id, StringComparison.Ordinal))
            {
                value = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Parse one id, or <b>throw</b>. For a path that has already validated the vocabulary and
    /// wants a refusal to be a defect rather than a boolean to swallow.
    /// </summary>
    public static AffixPowerClass Parse(string? id)
    {
        if (TryParse(id, out var value)) return value;
        throw new AffixPowerClassRejection(
            $"'{id}' is not a power class. The closed vocabulary is: {string.Join(", ", All)}.");
    }

    /// <summary>Ordinal position in the vocabulary, or -1 for a value it does not declare. The
    /// comparison a <c>MAX</c>-over-refs derivation orders by — never a re-derived index.</summary>
    public static int OrdinalOf(AffixPowerClass value) => IsDefined(value) ? (int)value : -1;
}

/// <summary>One row of the checked-in registry, resolved against the mirror.</summary>
/// <param name="Id">The registry's id — the same string <see cref="AffixPowerClassIds.All"/> carries.</param>
/// <param name="Ordinal">The structural ordinal the registry declares.</param>
/// <param name="Value">The mirror enum member this row resolves to.</param>
/// <param name="Meaning">The class's plain-language sense, carried for readers and tooling. It is
/// prose, never a number: the share this class is expected to hold is a tuning target in
/// <c>gk-core/data/tuning/</c>, not a field here.</param>
public readonly record struct AffixPowerClassRow(string Id, int Ordinal, AffixPowerClass Value, string Meaning);

/// <summary>
/// A pure parser over <c>gk-data/packs/fusion/data/seed/items/_registry/power-classes.v1.json</c> — no file I/O, per
/// <c>tunables-ssot.md</c> §7.2 ("Core never reads a file. Hosts load and inject.").
///
/// <para>Its real job is the <b>mirror check</b>: the document must declare exactly the ids and the
/// consecutive ordinals <see cref="AffixPowerClass"/> declares, in the same order. A registry that
/// adds, renames, renumbers or reorders a class is refused here rather than read by a consumer that
/// would then hold a class the enum cannot name.</para>
/// </summary>
public static class AffixPowerClassRegistry
{
    /// <summary>Read the registry and resolve every row against the mirror.</summary>
    /// <exception cref="AffixPowerClassRejection">The document is not valid JSON, has no
    /// <c>classes</c> array, or declares a roster that is not exactly the mirror's.</exception>
    public static IReadOnlyList<AffixPowerClassRow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AffixPowerClassRejection("power-classes registry: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new AffixPowerClassRejection($"power-classes registry: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new AffixPowerClassRejection("power-classes registry: root must be an object");

            if (!doc.RootElement.TryGetProperty("schemaVersion", out var schemaEl)
                || schemaEl.ValueKind != JsonValueKind.Number
                || !schemaEl.TryGetInt32(out var schemaVersion)
                || schemaVersion != 1)
                throw new AffixPowerClassRejection("power-classes registry: unsupported or missing schemaVersion");

            if (!doc.RootElement.TryGetProperty("registryVersion", out var registryEl)
                || registryEl.ValueKind != JsonValueKind.Number
                || !registryEl.TryGetInt32(out var registryVersion)
                || registryVersion < 1)
                throw new AffixPowerClassRejection("power-classes registry: missing or invalid registryVersion");

            if (!doc.RootElement.TryGetProperty("appendOnly", out var appendOnlyEl)
                || appendOnlyEl.ValueKind != JsonValueKind.True)
                throw new AffixPowerClassRejection("power-classes registry: appendOnly must be true");

            RejectMagnitudeNumbers(doc.RootElement, "$");

            if (!doc.RootElement.TryGetProperty("classes", out var classesEl)
                || classesEl.ValueKind != JsonValueKind.Array)
                throw new AffixPowerClassRejection("power-classes registry: missing or non-array 'classes'");

            var ids = new List<string>();
            var rows = new List<AffixPowerClassRow>(AffixPowerClassIds.Count);

            foreach (var el in classesEl.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                    throw new AffixPowerClassRejection("power-classes registry: a 'classes' entry is not an object");

                var id = String(el, "id");
                if (!AffixPowerClassIds.TryParse(id, out var value))
                    throw new AffixPowerClassRejection(
                        $"power-classes registry: '{id}' is not a power class. "
                        + $"The closed vocabulary is: {string.Join(", ", AffixPowerClassIds.All)}.");

                if (ids.Contains(id, StringComparer.Ordinal))
                    throw new AffixPowerClassRejection($"power-classes registry: '{id}' is declared twice");

                ids.Add(id);
                rows.Add(new AffixPowerClassRow(id, Int(el, "ordinal", id), value, String(el, "meaning")));
            }

            if (ids.Count != AffixPowerClassIds.Count)
                throw new AffixPowerClassRejection(
                    $"power-classes registry: declares {ids.Count} classes, the mirror declares "
                    + $"{AffixPowerClassIds.Count}. A sixth class is a reviewed change — the enum, this "
                    + "file and its tests move together, never one of them alone.");

            if (!ids.SequenceEqual(AffixPowerClassIds.All, StringComparer.Ordinal))
                throw new AffixPowerClassRejection(
                    $"power-classes registry: roster [{string.Join(", ", ids)}] does not match the mirror "
                    + $"[{string.Join(", ", AffixPowerClassIds.All)}] in the same order.");

            // Consecutive, 0..Count-1, and equal to the enum member's own value. A gap or a
            // renumbering is refused because the ordinal is what MAX orders by.
            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].Ordinal != i)
                    throw new AffixPowerClassRejection(
                        $"power-classes registry: '{rows[i].Id}' declares ordinal {rows[i].Ordinal} at position "
                        + $"{i}. Ordinals are consecutive from 0 and are never renumbered — an ordinal is the "
                        + "order MAX-over-refs compares by.");

                if (rows[i].Ordinal != (int)rows[i].Value)
                    throw new AffixPowerClassRejection(
                        $"power-classes registry: '{rows[i].Id}' ordinal {rows[i].Ordinal} disagrees with the "
                        + $"mirror's {(int)rows[i].Value}.");
            }

            return rows;
        }
    }

    static void RejectMagnitudeNumbers(JsonElement node, string path)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Name is not ("schemaVersion" or "registryVersion" or "ordinal"))
                            throw new AffixPowerClassRejection(
                                $"power-classes registry: numeric field '{path}.{prop.Name}' is not structural metadata");
                        continue;
                    }

                    RejectMagnitudeNumbers(prop.Value, $"{path}.{prop.Name}");
                }
                break;
            case JsonValueKind.Array:
                for (var i = 0; i < node.GetArrayLength(); i++)
                {
                    var item = node[i];
                    if (item.ValueKind == JsonValueKind.Number)
                        throw new AffixPowerClassRejection(
                            $"power-classes registry: numeric array item '{path}[{i}]' is not structural metadata");
                    RejectMagnitudeNumbers(item, $"{path}[{i}]");
                }
                break;
        }
    }

    static string String(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()!
            : throw new AffixPowerClassRejection($"power-classes registry: entry missing or non-string '{key}'");

    static int Int(JsonElement parent, string key, string id)
    {
        if (parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            return v;
        throw new AffixPowerClassRejection($"power-classes registry: '{id}' missing or non-integer '{key}'");
    }
}
