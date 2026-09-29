using System.Text.Json;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// D1.10's real remaining scope, event half (2026-09-07): reads `gk-data/packs/fusion/data/seed/dungeon/events/*.json`
/// into <see cref="EventRow"/> rows -- the same direct-file-read shape `Quests.QuestSeedFile`/
/// `Roll.LayoutSeedFile` already use for committed seed content, proving real seedsmith-authored
/// content against the real <see cref="EventCatalog"/> validator rather than a hand-built fixture.
///
/// <para>String-typed nullable fields (`climateAffinity`, `supplyOverride`, `chainRef`) use this
/// program's own `"none"` sentinel, converted to a real C# <c>null</c> here exactly as
/// `QuestSeedFile` already does. <c>eligibility</c> is different: its C# type is the OBJECT-typed
/// <see cref="FusionRpg.Core.Effects.Atoms.PredicateNode"/>?, so its seed-JSON representation is a
/// real JSON <c>null</c>, never the string `"none"` — `EventRow.cs`'s own doc comment already states
/// this ("`null` means 'always eligible'"). This loader reads ONLY that null case today: no
/// generator anywhere in this pipeline emits a real predicate tree yet (`briefs.py`'s own
/// `build_event_schema_for_cell` pins `eligibility` to `const: null` for the whole first-ship
/// batch), so a committed row carrying an actual object here would mean a FUTURE batch shipped a
/// tree this loader was never extended to parse — refusing loudly is correct, not a missing
/// feature silently swallowed.</para>
/// </summary>
public static class EventSeedFile
{
    static string? NoneToNull(string value) => value == "none" ? null : value;

    public static IReadOnlyList<EventRow> LoadAll(string eventsDir)
    {
        if (eventsDir is null) throw new ArgumentNullException(nameof(eventsDir));
        if (!Directory.Exists(eventsDir)) return Array.Empty<EventRow>();

        var rows = new List<EventRow>();
        foreach (var path in Directory.EnumerateFiles(eventsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;

            var eligibilityEl = root.GetProperty("eligibility");
            if (eligibilityEl.ValueKind != JsonValueKind.Null)
                throw new NotSupportedException(
                    $"{path}: a real 'eligibility' tree is not supported by this loader yet -- " +
                    "no generator in this pipeline emits one; only JSON null ('always eligible') is read today.");

            var outcomes = new List<EventOutcomeRow>();
            foreach (var outcomeEl in root.GetProperty("outcomes").EnumerateArray())
            {
                var effects = new List<EventEffectRef>();
                foreach (var effectEl in outcomeEl.GetProperty("effects").EnumerateArray())
                {
                    effects.Add(new EventEffectRef(
                        effectEl.GetProperty("family").GetString()!,
                        effectEl.GetProperty("powerBand").GetString()!));
                }
                outcomes.Add(new EventOutcomeRow(
                    Ordinal: outcomeEl.GetProperty("ordinal").GetString()!,
                    DropBand: outcomeEl.GetProperty("dropBand").GetString()!,
                    Consequence: outcomeEl.GetProperty("consequence").GetString()!,
                    Effects: effects));
            }

            rows.Add(new EventRow(
                EventId: root.GetProperty("eventId").GetString()!,
                Kind: root.GetProperty("kind").GetString()!,
                Theme: NoneToNull(root.GetProperty("theme").GetString()!),
                ClimateAffinity: NoneToNull(root.GetProperty("climateAffinity").GetString()!),
                RepeatScope: root.GetProperty("repeatScope").GetString()!,
                Eligibility: null,
                Outcomes: outcomes,
                SupplyOverride: NoneToNull(root.GetProperty("supplyOverride").GetString()!),
                ChainRef: NoneToNull(root.GetProperty("chainRef").GetString()!)));
        }
        return rows;
    }
}
