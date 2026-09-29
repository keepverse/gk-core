using System.Text.Json;

namespace FusionRpg.Core.Creatures;

public sealed record SoulKillTuning(int KillDelta);

public sealed record SoulMatchEndTuning(int VictoryDelta, int DefeatDelta);

public sealed record SoulCodexTuning(int HalfMilestone, int FullMilestone);

/// <summary>Souls earn balance surface (tunables-ssot.md T1) — loaded, not hard-coded. See
/// <see cref="SoulEarnPolicy.Configure"/> and <see cref="SoulEarnTuningLoader"/>.</summary>
public sealed record SoulEarnTuning(
    int SchemaVersion, int Version,
    SoulKillTuning Kill, SoulMatchEndTuning MatchEnd,
    IReadOnlyDictionary<CreatureRarity, int> DiscoveryDelta, SoulCodexTuning Codex);

public sealed class SoulEarnTuningRejection : Exception
{
    public SoulEarnTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class SoulEarnTuningLoader
{
    public static SoulEarnTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SoulEarnTuningRejection("souls tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SoulEarnTuningRejection($"souls tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var k = Obj(root, "kill", "$");
            var kill = new SoulKillTuning(
                KillDelta: Int(k, "killDelta", "kill"));

            var m = Obj(root, "matchEnd", "$");
            var matchEnd = new SoulMatchEndTuning(
                VictoryDelta: Int(m, "victoryDelta", "matchEnd"),
                DefeatDelta: Int(m, "defeatDelta", "matchEnd"));

            var dEl = Obj(root, "discoveryDelta", "$");
            var discovery = new Dictionary<CreatureRarity, int>();
            foreach (var rarity in Enum.GetValues<CreatureRarity>())
                discovery[rarity] = Int(dEl, rarity.ToString().ToLowerInvariant(), "discoveryDelta");

            var c = Obj(root, "codex", "$");
            var codex = new SoulCodexTuning(
                HalfMilestone: Int(c, "halfMilestone", "codex"),
                FullMilestone: Int(c, "fullMilestone", "codex"));

            return new SoulEarnTuning(schemaVersion, version, kill, matchEnd, discovery, codex);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new SoulEarnTuningRejection($"souls tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new SoulEarnTuningRejection($"souls tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}
