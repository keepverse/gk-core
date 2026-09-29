using System.Text.Json.Nodes;

namespace FusionRpg.Tools.CombatSim;

/// <summary>
/// Loads the SHIPPED tuning files (gk-core/data/tuning/*.json) into Core's policy singletons, exactly the
/// way FusionRpg.Server's own startup does — so the sim measures what the game ships, not a copy.
///
/// <para><c>--set</c> overrides patch the JSON in memory BEFORE parsing rather than poking policy
/// properties afterwards. That works uniformly for every domain (including read-only policies like
/// ShieldPolicy whose values are projections of a record), needs no per-key plumbing, and mirrors
/// what gk-core/tools/tuning/publish.py writes — so an override that looks good here is one publish command
/// away from being real, with no translation step to get wrong.</para>
/// </summary>
public static class TuningBootstrap
{
    public static string RepoRoot { get; private set; } = "";

    public static void Load(IReadOnlyList<string> overrides)
    {
        // Re-callable: `compare` reconfigures the policy singletons once per variant. Touched must
        // reset or variant 2 would be told variant 1's domains were already used.
        Touched.Clear();
        RepoRoot = FindRepoRoot();
        var dir = Path.Combine(RepoRoot, "data", "tuning");
        var patches = ParseOverrides(overrides);

        FusionRpg.Core.Combat.CombatPolicy.Configure(
            FusionRpg.Core.Combat.CombatTuningLoader.Parse(Read(dir, "combat", patches)));
        FusionRpg.Core.Combat.Shield.ShieldPolicy.Configure(
            FusionRpg.Core.Combat.Shield.ShieldTuningLoader.Parse(Read(dir, "shield", patches)));
        FusionRpg.Core.Stats.Derived.StatsTuningHub.Configure(
            FusionRpg.Core.Stats.Derived.StatsTuningLoader.Parse(Read(dir, "stats", patches)));
        FusionRpg.Core.Stats.Derived.DerivedStatPolicy.Configure(
            FusionRpg.Core.Stats.Derived.DerivedStatTuningLoader.Parse(Read(dir, "derived-stats", patches, version: 2)));
        FusionRpg.Core.Status.StatusPolicy.Configure(
            FusionRpg.Core.Status.StatusTuningLoader.Parse(Read(dir, "status", patches)));

        var unused = patches.Keys.Where(k => !Touched.Contains(k)).ToList();
        if (unused.Count > 0)
            throw new InvalidOperationException(
                "--set names a domain with no tuning file loaded here: " + string.Join(", ", unused) +
                ". Known domains: combat, shield, stats, derived-stats, status.");
    }

    static readonly HashSet<string> Touched = new(StringComparer.Ordinal);

    /// <summary><paramref name="version"/> is per-domain and explicit rather than "resolve the latest
    /// on disk", because this tool pins its inputs on purpose: `aptitudes` is at v5 and CombatSim
    /// deliberately does not read it here, and silently following the newest file would move every
    /// class-system baseline the next time any domain is published. Only `derived-stats` is at v2, and
    /// only because T14/B28 added `turnDefaultSpeed` to it as a schema change.</summary>
    static string Read(string dir, string domain, Dictionary<string, Dictionary<string, string>> patches, int version = 1)
    {
        var path = Path.Combine(dir, domain + ".v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json");
        var text = File.ReadAllText(path);
        if (!patches.TryGetValue(domain, out var keys)) return text;
        Touched.Add(domain);

        var node = JsonNode.Parse(text)?.AsObject()
                   ?? throw new InvalidOperationException($"{path}: not a JSON object");
        foreach (var (dotted, raw) in keys)
            SetDotted(node, dotted, raw, path);
        return node.ToJsonString();
    }

    static void SetDotted(JsonObject root, string dotted, string raw, string path)
    {
        var parts = dotted.Split('.');
        var cursor = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            cursor = cursor[parts[i]]?.AsObject()
                     ?? throw new InvalidOperationException($"{path}: no object at '{parts[i]}' in '{dotted}'");
        }
        var leaf = parts[^1];
        if (!cursor.ContainsKey(leaf))
            throw new InvalidOperationException(
                $"{path}: no key '{leaf}' — refusing to invent a tunable the game does not read");
        // Type must match what the loader expects for that key: a bool key rejects a string "true",
        // and a numeric key rejects a quoted number. Infer from the literal rather than from the
        // existing node's kind, so a key can still be given a genuinely new type when that is meant.
        cursor[leaf] = bool.TryParse(raw, out var b)
            ? JsonValue.Create(b)
            : double.TryParse(raw, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? JsonValue.Create(d)
                : JsonValue.Create(raw)!;
    }

    /// <summary>"combat.pierceScale=500" → { combat: { pierceScale: "500" } }</summary>
    static Dictionary<string, Dictionary<string, string>> ParseOverrides(IReadOnlyList<string> overrides)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var o in overrides)
        {
            var eq = o.IndexOf('=');
            if (eq <= 0) throw new InvalidOperationException($"--set expects <domain>.<key>=<value>, got '{o}'");
            var lhs = o[..eq];
            var value = o[(eq + 1)..];
            var dot = lhs.IndexOf('.');
            if (dot <= 0) throw new InvalidOperationException($"--set expects <domain>.<key>=<value>, got '{o}'");
            var domain = lhs[..dot];
            var key = lhs[(dot + 1)..];
            if (!result.TryGetValue(domain, out var map))
                result[domain] = map = new Dictionary<string, string>(StringComparer.Ordinal);
            map[key] = value;
        }
        return result;
    }

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data", "tuning"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repo root (looked for data/tuning above the exe)");
    }
}
