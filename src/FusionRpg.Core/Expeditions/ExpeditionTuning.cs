using System.Text.Json;

namespace FusionRpg.Core.Expeditions;

public sealed record ExpeditionTierNumbers(int DurationMinutes, int TickCount, int BattleCount, int SquadSlots, int DangerBand);

public sealed record ExpeditionEventRollTuning(
    int QuietCeilMilli, int FoundSoulsCeilMilli, int WildCeilMilli, int WildJoinMilli,
    int ShinyDie, int InjuryPowerDivisor);

/// <summary>
/// The encounter chances (spec-expedition-lead-host.md §6, published with the tier danger bands by
/// plan §4 D4): the per-mille chance a wild tick meets a creature that can become a lead, and the
/// per-mille chance a quiet tick yields a rumour. Bounded ratios, so <c>int</c> per-mille is their
/// own type — nothing here is a magnitude on the power ladder.
///
/// <para>Required, like every other key in this file: the reader that spends them lands with
/// <c>expedition-lead-host</c>, and a missing key rejects by name rather than defaulting to a chance
/// nobody chose (tunables-ssot.md T5).</para>
/// </summary>
public sealed record ExpeditionEncounterTuning(int WildCreatureMetMilli, int QuietMilli);

/// <summary>Expeditions balance surface (tunables-ssot.md T1) — loaded, not hard-coded. Tier ids/
/// names/hasBossWave stay in <see cref="ExpeditionTierCatalog"/> (schema); their numbers, and
/// <see cref="ExpeditionResolver"/>'s event-roll bands, live here. See
/// <see cref="ExpeditionTuningHub.Configure"/> and <see cref="ExpeditionTuningLoader"/>.</summary>
public sealed record ExpeditionTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, ExpeditionTierNumbers> Tiers,
    ExpeditionEventRollTuning EventRoll,
    ExpeditionEncounterTuning Encounter);

public sealed class ExpeditionTuningRejection : Exception
{
    public ExpeditionTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class ExpeditionTuningLoader
{
    static readonly string[] TierIds = { "scout-30m", "forage-4h", "hunt-8h", "warpath-20h" };

    public static ExpeditionTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ExpeditionTuningRejection("expeditions tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new ExpeditionTuningRejection($"expeditions tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var tiersEl = Obj(root, "tiers", "$");
            var tiers = new Dictionary<string, ExpeditionTierNumbers>(StringComparer.Ordinal);
            foreach (var tierId in TierIds)
            {
                var t = Obj(tiersEl, tierId, "tiers");
                tiers[tierId] = new ExpeditionTierNumbers(
                    DurationMinutes: Int(t, "durationMinutes", $"tiers.{tierId}"),
                    TickCount: Int(t, "tickCount", $"tiers.{tierId}"),
                    BattleCount: Int(t, "battleCount", $"tiers.{tierId}"),
                    SquadSlots: Int(t, "squadSlots", $"tiers.{tierId}"),
                    DangerBand: Int(t, "dangerBand", $"tiers.{tierId}"));
            }

            var e = Obj(root, "eventRoll", "$");
            var eventRoll = new ExpeditionEventRollTuning(
                QuietCeilMilli: Int(e, "quietCeilMilli", "eventRoll"),
                FoundSoulsCeilMilli: Int(e, "foundSoulsCeilMilli", "eventRoll"),
                WildCeilMilli: Int(e, "wildCeilMilli", "eventRoll"),
                WildJoinMilli: Int(e, "wildJoinMilli", "eventRoll"),
                ShinyDie: Int(e, "shinyDie", "eventRoll"),
                InjuryPowerDivisor: Int(e, "injuryPowerDivisor", "eventRoll"));

            var encounterEl = Obj(root, "encounter", "$");
            var encounter = new ExpeditionEncounterTuning(
                WildCreatureMetMilli: Int(encounterEl, "wildCreatureMetMilli", "encounter"),
                QuietMilli: Int(encounterEl, "quietMilli", "encounter"));

            return new ExpeditionTuning(Int(root, "schemaVersion", "$"), Int(root, "version", "$"),
                tiers, eventRoll, encounter);
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new ExpeditionTuningRejection($"expeditions tuning: missing or non-object '{path}.{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new ExpeditionTuningRejection($"expeditions tuning: missing or non-integer '{path}.{key}'");
        return v;
    }
}

/// <summary>Single configuration point covering <see cref="ExpeditionTierCatalog"/> and
/// <see cref="ExpeditionResolver"/>, which share one <c>expeditions.v{n}.json</c>.</summary>
public static class ExpeditionTuningHub
{
    static ExpeditionTuning? _tuning;

    public static void Configure(ExpeditionTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static ExpeditionTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "ExpeditionTuningHub.Configure(...) has not run. Every expedition rule reads data/tuning/" +
        "expeditions.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}
