using System.Text.Json;

namespace FusionRpg.Tools.CombatSim;

/// <summary>One candidate: a name and the tuning patches that define it.</summary>
public sealed class Variant
{
    public string Name { get; set; } = "unnamed";
    public string? Note { get; set; }
    /// <summary><c>domain.key=value</c> entries, same grammar as <c>--set</c>.</summary>
    public List<string> Set { get; set; } = new();
}

/// <summary>A named comparison — the reusable artifact of a balance decision.</summary>
public sealed class VariantSet
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }
    /// <summary>Default scenario; <c>--scenario</c> overrides it.</summary>
    public string? Scenario { get; set; }
    public List<Variant> Variants { get; set; } = new();

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static VariantSet Load(string nameOrPath)
    {
        var path = Resolve(nameOrPath);
        var set = JsonSerializer.Deserialize<VariantSet>(File.ReadAllText(path), Options)
                  ?? throw new InvalidOperationException($"{path}: empty variant set");
        if (set.Variants.Count == 0)
            throw new InvalidOperationException($"{path}: no variants");
        return set;
    }

    static string Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        var file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? nameOrPath : nameOrPath + ".json";
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "variants"),
                     Path.Combine(TuningBootstrap.RepoRoot, "tools", "CombatSim", "variants")
                 })
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"variant set '{nameOrPath}' not found");
    }
}
