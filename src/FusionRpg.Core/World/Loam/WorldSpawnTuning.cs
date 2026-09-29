using System.Text.Json;
using FusionRpg.Core.Creatures;

namespace FusionRpg.Core.World.Loam;

/// <summary>A future per-species spawn-table entry (species-gear-chain T7, spec-wild-species-spawn.md
/// "Introduced, not designed"): demons, void beasts, any neutral that must NEVER become a legion
/// troop. UNPOPULATED today — the shape is the deliverable. <c>Recruitable</c> defaults true so the
/// ordinary path stays ordinary; the hunt interaction (a later program) reads the flag, the spawn
/// roll only carries it.</summary>
public sealed record WorldSpawnMemberEntry(string SpeciesId, int WeightMilli, bool Recruitable = true);

/// <summary>One sector type's spawn row: a rarity window with relative rung weights, the same
/// slot-table shape as wave-species-roll, plus the future per-species member list.</summary>
public sealed record WorldSpawnSectorTuning(
    CreatureRarity RarityFrom, CreatureRarity RarityTo,
    IReadOnlyDictionary<string, int> Weights, long SameSpeciesMaxMilli,
    IReadOnlyList<WorldSpawnMemberEntry> Members);

/// <summary>Wild-spawn balance surface (species-gear-chain T7) — loaded, not hard-coded. Sector
/// type ids stay content (an unknown sector type is an empty pool, hence the named fallback, never
/// a load refusal); only the numeric fields and the window bounds live here.</summary>
public sealed record WorldSpawnTuning(
    int SchemaVersion, int Version, long OffClimateMilli, string FallbackSpeciesId,
    IReadOnlyDictionary<string, WorldSpawnSectorTuning> Sectors);

public sealed class WorldSpawnTuningRejection : Exception
{
    public WorldSpawnTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class WorldSpawnTuningLoader
{
    public static WorldSpawnTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new WorldSpawnTuningRejection("world spawn tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new WorldSpawnTuningRejection($"world spawn tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = RequireInt(root, "schemaVersion");
            var version = RequireInt(root, "version");
            var offClimate = RequireMilli(root, "offClimateMilli");
            var fallback = RequireString(root, "fallbackSpeciesId");

            if (!root.TryGetProperty("sectors", out var sectorsEl) || sectorsEl.ValueKind != JsonValueKind.Object)
                throw new WorldSpawnTuningRejection("world spawn tuning: missing or non-object 'sectors'");

            var sectors = new Dictionary<string, WorldSpawnSectorTuning>(StringComparer.Ordinal);
            foreach (var sector in sectorsEl.EnumerateObject())
            {
                var from = RequireRung(sector.Value, "rarityFrom", sector.Name);
                var to = RequireRung(sector.Value, "rarityTo", sector.Name);
                if (CreatureRarityLadder.RungsBetween(from, to).Count == 0)
                    throw new WorldSpawnTuningRejection(
                        $"world spawn tuning: sector '{sector.Name}' has an inverted rarity window");
                var weights = RequireWeights(sector.Value, sector.Name, from, to);
                var cap = RequireCappedMilli(sector.Value, "sameSpeciesMaxMilli", sector.Name);
                var members = RequireMembers(sector.Value, sector.Name);
                sectors[sector.Name] = new WorldSpawnSectorTuning(from, to, weights, cap, members);
            }

            if (sectors.Count == 0)
                throw new WorldSpawnTuningRejection("world spawn tuning: 'sectors' names no sector type");

            return new WorldSpawnTuning(schemaVersion, version, offClimate, fallback, sectors);
        }
    }

    static IReadOnlyDictionary<string, int> RequireWeights(JsonElement el, string sector, CreatureRarity from, CreatureRarity to)
    {
        if (!el.TryGetProperty("weights", out var w) || w.ValueKind != JsonValueKind.Object)
            throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' is missing or has a non-object 'weights'");
        var weights = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var rung in CreatureRarityLadder.RungsBetween(from, to))
        {
            var id = rung.ToId();
            if (!w.TryGetProperty(id, out var wv) || wv.ValueKind != JsonValueKind.Number)
                throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' weights have no entry for in-window rung '{id}'");
            var weight = wv.GetInt32();
            if (weight < 0)
                throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' weights['{id}'] is negative ({weight})");
            weights[id] = weight;
        }
        foreach (var name in w.EnumerateObject().Select(p => p.Name))
            if (!weights.ContainsKey(name))
                throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' weights name unknown rung '{name}'");
        if (weights.Values.All(v => v == 0))
            throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' weights are all zero — nothing is drawable");
        return weights;
    }

    static IReadOnlyList<WorldSpawnMemberEntry> RequireMembers(JsonElement el, string sector)
    {
        // Absent is an empty list, not an error — no members are authored today, and the shape
        // must load cleanly both before and after the void-raid program populates it.
        if (!el.TryGetProperty("members", out var m) || m.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorldSpawnMemberEntry>();
        var members = new List<WorldSpawnMemberEntry>();
        foreach (var e in m.EnumerateArray())
        {
            var id = RequireString(e, "speciesId");
            if (!e.TryGetProperty("weightMilli", out var wv) || wv.ValueKind != JsonValueKind.Number)
                throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' member '{id}' is missing weightMilli");
            var weight = wv.GetInt32();
            if (weight <= 0)
                throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' member '{id}' has non-positive weightMilli ({weight}) — mute by removing the entry, never by zeroing it");
            var recruitable = true;
            if (e.TryGetProperty("recruitable", out var rv))
            {
                if (rv.ValueKind != JsonValueKind.True && rv.ValueKind != JsonValueKind.False)
                    throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' member '{id}' has a non-boolean 'recruitable'");
                recruitable = rv.GetBoolean();
            }
            members.Add(new WorldSpawnMemberEntry(id, weight, recruitable));
        }
        return members;
    }

    static CreatureRarity RequireRung(JsonElement el, string prop, string sector)
    {
        var text = RequireString(el, prop);
        if (!CreatureRarityIds.TryParse(text, out var rung))
            throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' names unknown rung '{text}'");
        return rung;
    }

    static long RequireMilli(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new WorldSpawnTuningRejection($"world spawn tuning: missing or non-numeric '{prop}'");
        var milli = v.GetInt64();
        if (milli < 0 || milli > 1000)
            throw new WorldSpawnTuningRejection($"world spawn tuning: '{prop}' must be a per-mille share in [0..1000], got {milli}");
        return milli;
    }

    static long RequireCappedMilli(JsonElement el, string prop, string sector)
    {
        if (!el.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.Number)
            throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' is missing or has non-numeric '{prop}'");
        var milli = v.GetInt64();
        if (milli < 0 || milli > 1000)
            throw new WorldSpawnTuningRejection($"world spawn tuning: sector '{sector}' '{prop}' must be in [0..1000], got {milli}");
        return milli;
    }

    static string RequireString(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!
            : throw new WorldSpawnTuningRejection($"world spawn tuning: missing or empty '{prop}'");

    static int RequireInt(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : throw new WorldSpawnTuningRejection($"world spawn tuning: missing or non-numeric '{prop}'");
}

/// <summary>Static holder mirroring <see cref="WorldTuningHub"/> — configured once at startup.</summary>
public static class WorldSpawnTuningHub
{
    static WorldSpawnTuning? _tuning;

    public static void Configure(WorldSpawnTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static WorldSpawnTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "WorldSpawnTuningHub.Configure(...) has not run. Wild-spawn weights read the hub like every " +
        "other world number; tests configure it per-test or through the shared bootstrap.");
}
