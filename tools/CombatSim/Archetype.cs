using System.Text.Json;
using System.Text.Json.Serialization;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// One build, as a fighter rather than as an attacker or a defender. `fight` mode has a fixed
/// attacker and a defender that never swings back — fine for measuring one exchange, useless for
/// asking "does FINESSE actually beat FORCE", which needs both sides trying to win.
/// </summary>
public sealed class Archetype
{
    public string Name { get; set; } = "unnamed";
    public string? Description { get; set; }

    public StatRange Hp { get; set; } = StatRange.Fixed(100_000);
    public StatRange BaseDamage { get; set; } = StatRange.Fixed(1_000);
    public StatRange ShieldHp { get; set; } = StatRange.Fixed(0);

    /// <summary>Element this build attacks with. Its own typing for matchup purposes too.</summary>
    public string? Element { get; set; }

    /// <summary>Any registered channel id → value. Validated against the live registry on load.</summary>
    public Dictionary<string, StatRange> Stats { get; set; } = new(StringComparer.Ordinal);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Archetype Load(string nameOrPath)
    {
        var path = Resolve(nameOrPath);
        var a = JsonSerializer.Deserialize<Archetype>(File.ReadAllText(path), Options)
                ?? throw new InvalidOperationException($"{path}: empty archetype");

        // Same discipline as Scenario.Validate: a typo'd channel would be silently ignored and make
        // every number this tool prints wrong in a way nobody could see.
        var registry = DerivedStatRegistry.CreateDefault();
        var bad = a.Stats.Keys.Where(id => !registry.TryResolveChannel(id, out _)).ToList();
        if (bad.Count > 0)
            throw new InvalidOperationException($"{path}: unregistered channel id(s): {string.Join(", ", bad)}");
        if (a.Element != null && !ElementRoster.TryParse(a.Element, out _))
            throw new InvalidOperationException($"{path}: unknown element '{a.Element}'");
        return a;
    }

    static string Resolve(string nameOrPath)
    {
        if (File.Exists(nameOrPath)) return nameOrPath;
        var file = nameOrPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? nameOrPath : nameOrPath + ".json";
        foreach (var dir in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "archetypes"),
                     Path.Combine(TuningBootstrap.RepoRoot, "tools", "CombatSim", "archetypes")
                 })
        {
            var candidate = Path.Combine(dir, file);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"archetype '{nameOrPath}' not found");
    }
}

/// <summary>Outcome of many duels between two archetypes.</summary>
public sealed class DuelSummary
{
    public required string A { get; init; }
    public required string B { get; init; }
    public required int Duels { get; init; }
    public double AWins { get; init; }
    public double BWins { get; init; }
    public double MutualKills { get; init; }
    public double Stalemates { get; init; }
    public double MedianRounds { get; init; }

    /// <summary>Measured HP lost per round, per side — the diagnostic that separates "the model has
    /// the wrong rate" from "the model has the wrong variance". A win-share residual alone cannot.</summary>
    public double RateAgainstA { get; init; }
    public double RateAgainstB { get; init; }

    /// <summary>A's share of decisive results — the number the matrix prints. 0.5 is a coin flip.</summary>
    public double AWinShare => AWins + BWins <= 0 ? 0.5 : AWins / (AWins + BWins);
}
