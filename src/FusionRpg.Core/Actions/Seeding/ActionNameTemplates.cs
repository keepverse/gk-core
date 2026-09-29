using System.Text.Json;

namespace FusionRpg.Core.Actions.Seeding;

public sealed class ActionNameTemplateRejection : Exception
{
    public ActionNameTemplateRejection(string message) : base(message) { }
}

/// <summary>
/// T31 (spec-action-seeding.md §2.1): "identity at runtime is template composition, NOT a model" —
/// nothing calls anything non-deterministic mid-roll, and two players on the same seed must see the
/// same text. <c>Sharp Sword of the Bear</c> is affix templates composed by rule; a rolled action's
/// name composes from its atoms' family templates the same way.
///
/// <para>The FIRST picked atom is the base (its own bare name); every atom after it is a rider whose
/// template wraps the name built so far. An atom family with no authored template — base or modifier
/// — REJECTS rather than composing a fallback string: an unnamed rider is the naming equivalent of an
/// unshared magnitude (spec §7: "never a default for a missing share").</para>
/// </summary>
public sealed class ActionNameTemplates
{
    readonly IReadOnlyDictionary<string, string> _base;
    readonly IReadOnlyDictionary<string, string> _modifier;

    ActionNameTemplates(IReadOnlyDictionary<string, string> baseNames, IReadOnlyDictionary<string, string> modifiers)
    {
        _base = baseNames;
        _modifier = modifiers;
    }

    /// <summary><paramref name="atomFamilyIds"/> in pick order — index 0 is the base, the rest are
    /// riders applied in order so composition is deterministic for a given draw order.</summary>
    public string Compose(IReadOnlyList<string> atomFamilyIds)
    {
        if (atomFamilyIds.Count == 0)
            throw new ActionNameTemplateRejection("cannot compose a name from zero atoms");

        var baseFamily = atomFamilyIds[0];
        if (!_base.TryGetValue(baseFamily, out var name))
            throw new ActionNameTemplateRejection($"no authored base name template for atom family '{baseFamily}'");

        for (var i = 1; i < atomFamilyIds.Count; i++)
        {
            var riderFamily = atomFamilyIds[i];
            if (!_modifier.TryGetValue(riderFamily, out var template))
                throw new ActionNameTemplateRejection($"no authored modifier template for atom family '{riderFamily}'");
            name = template.Replace("{name}", name, StringComparison.Ordinal);
        }

        return name;
    }

    public static ActionNameTemplates Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ActionNameTemplateRejection("action name templates: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new ActionNameTemplateRejection($"action name templates: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var baseNames = ReadStringMap(root, "base");
            var modifiers = ReadStringMap(root, "modifiers");
            foreach (var kv in modifiers)
            {
                if (!kv.Value.Contains("{name}", StringComparison.Ordinal))
                    throw new ActionNameTemplateRejection(
                        $"action name templates: modifier '{kv.Key}' has no '{{name}}' placeholder — it could never wrap a base name");
            }
            return new ActionNameTemplates(baseNames, modifiers);
        }
    }

    static Dictionary<string, string> ReadStringMap(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var section) || section.ValueKind != JsonValueKind.Object)
            throw new ActionNameTemplateRejection($"action name templates: missing or non-object '{key}'");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in section.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.String)
                throw new ActionNameTemplateRejection($"action name templates: '{key}.{prop.Name}' is not a string");
            map[prop.Name] = prop.Value.GetString()!;
        }
        return map;
    }
}
