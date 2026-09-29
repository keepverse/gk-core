using System.Text.Json;
using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Materials;

namespace FusionRpg.Core.Expeditions;

/// <summary>
/// species-gear-chain T31 (`creature-drop-tables` E3a) — the rung → shard map, owned by this module
/// alone (spec-creature-drop-tables.md § Tunables, strengthen pass 2026-09-18: "shared with
/// `species-materials`" was two owners for one file; `species-materials` reads nothing from it).
///
/// <para>A ten-row identity map (<c>chaff → shard.chaff</c>, … <c>almanac → shard.almanac</c>) is the
/// legal first value — the shard ids already ship (<see cref="MaterialCatalog.ShardId"/> derives the
/// identical string structurally). This file exists as a TUNING surface, not a structural constant,
/// because a later balance pass may want to compress rungs into fewer material grades (MH's own
/// <c>Scale → Scale+ → Shard</c> history is the cited precedent) — a change to what a rung YIELDS,
/// never to the ladder itself.</para>
/// </summary>
public sealed record CreatureYieldTuning(
    int SchemaVersion, int Version, IReadOnlyDictionary<string, string> ShardByRung);

public sealed class CreatureYieldTuningRejection : Exception
{
    public CreatureYieldTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2). Throws on a missing, extra or
/// unresolvable rung — never defaults, per T5's "expected but missing is a rejection" posture.</summary>
public static class CreatureYieldTuningLoader
{
    public static CreatureYieldTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new CreatureYieldTuningRejection("creature-yield tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CreatureYieldTuningRejection($"creature-yield tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            if (!root.TryGetProperty("rungs", out var rungsEl) || rungsEl.ValueKind != JsonValueKind.Object)
                throw new CreatureYieldTuningRejection("creature-yield tuning: missing or non-object 'rungs'");

            var shardByRung = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var rungId in RarityLadder.RungIds)
            {
                if (!rungsEl.TryGetProperty(rungId, out var el) || el.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(el.GetString()))
                    throw new CreatureYieldTuningRejection(
                        $"creature-yield tuning: missing or empty 'rungs.{rungId}' — every one of the " +
                        "ten rungs must map to a shard id, a missing rung is a load rejection, never a default");

                var shardId = el.GetString()!;
                if (!MaterialCatalog.All.Contains(shardId, StringComparer.Ordinal))
                    throw new CreatureYieldTuningRejection(
                        $"creature-yield tuning: 'rungs.{rungId}' = '{shardId}' is not one of the 27 " +
                        "issuable material ids — this file may never widen that vocabulary");

                shardByRung[rungId] = shardId;
            }

            // Reject an unknown key outright rather than silently ignoring it — a typo'd rung id would
            // otherwise ship as dead content nobody notices.
            foreach (var prop in rungsEl.EnumerateObject())
                if (!RarityLadder.RungIds.Contains(prop.Name, StringComparer.Ordinal))
                    throw new CreatureYieldTuningRejection(
                        $"creature-yield tuning: 'rungs.{prop.Name}' is not one of the ten rung ids");

            return new CreatureYieldTuning(schemaVersion, version, shardByRung);
        }
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new CreatureYieldTuningRejection($"creature-yield tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}

/// <summary>Single configuration point for <see cref="ExpeditionResolver"/>'s E3a shard lookup.</summary>
public static class CreatureYieldTuningHub
{
    static CreatureYieldTuning? _tuning;

    public static void Configure(CreatureYieldTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static CreatureYieldTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "CreatureYieldTuningHub.Configure(...) has not run. ExpeditionResolver reads data/tuning/" +
        "creature-yield.v1.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");

    /// <summary>The shard a creature of this rung yields. Throws naming the rung on a lookup miss —
    /// unreachable once <see cref="CreatureYieldTuningLoader.Parse"/> has validated all ten rungs are
    /// present, but named rather than an index-out-of-range if that contract is ever broken.</summary>
    public static string ShardFor(string rungId) =>
        Tuning.ShardByRung.TryGetValue(rungId, out var shardId)
            ? shardId
            : throw new ArgumentException($"creature-yield: no shard mapped for rung '{rungId}'", nameof(rungId));
}
