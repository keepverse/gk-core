using System.Text.Json;
using FusionRpg.Core.Creatures.Generation;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// D1.10's real remaining scope, encounter half (2026-09-07): reads `gk-data/packs/fusion/data/seed/dungeon/
/// encounters/*.json` into <see cref="EncounterAnchor"/> — the same direct-file-read shape
/// `Events.EventSeedFile`/`Quests.QuestSeedFile`/`Roll.LayoutSeedFile` already use for committed
/// seed content.
///
/// <para><b>Unlike quest/event, this projects DOWN, not across.</b> `EncounterAnchor` is
/// `encounter-generator`'s own lean build-time DTO (`Encounter.cs:32-35`, explicitly documented as
/// NOT a JSON-loaded seed row — "tests construct this directly"), and it carries only
/// `Formation`/`Slots`/`RankOrder`/`ElementSpread`/`ThreatWindow`/`BossSpeciesRef`/`BossKit`. The
/// real seed anchor's own `encounterId`/`name`/`reason`/`tempo`/`synergyHint`/`affixRoll` fields
/// have NO corresponding field on this type at all — this loader reads and validates them for
/// shape (a malformed one still throws) but does not carry them forward, matching this program's
/// own repeated "shape rules run, container-kind binding does not" posture (`ConsumableCatalog`'s
/// own precedent) for authored-but-not-yet-consumed content. `BossSpeciesRef` is deliberately never
/// populated from the seed file — the seed contract's own words are "not a species — the domain
/// pins bossSpeciesRef at runtime" (§1.6), so it stays `null` here; a real domain-catalog caller
/// supplies it later.</para>
///
/// <para><b>`threatWindow` band names resolve to real rungs via <see cref="CreatureThreatTuning"/></b>
/// (passed in, never re-read from disk here) — the seed contract's own ten threat nouns, while
/// `EncounterAnchor.ThreatWindow` is `(int FloorRung, int CeilRung)` (`SlotFilter.cs:109`).</para>
///
/// <para><b>`boss.retinue` is a real, already-shipped index, never the spec's own stale
/// `{slotRef, countBand}` shape</b> — a real spec-vs-code drift found before this loader was
/// written (recorded in full in this program's own todo file, 2026-09-07): `BossKit.
/// RetinueSlotIndex` is a plain `int` position in the SAME `Slots` list, and this loader reads it
/// as exactly that.</para>
/// </summary>
public static class EncounterSeedFile
{
    static readonly Dictionary<string, EncounterReach?> Reaches = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = null, ["melee"] = EncounterReach.Melee, ["short"] = EncounterReach.Short,
        ["long"] = EncounterReach.Long, ["siege"] = EncounterReach.Siege,
    };

    static readonly Dictionary<string, TargetPreference?> TargetPreferences = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = null, ["frontline"] = TargetPreference.Frontline, ["backline"] = TargetPreference.Backline,
        ["swarm"] = TargetPreference.Swarm, ["elite"] = TargetPreference.Elite,
        ["structure"] = TargetPreference.Structure, ["indiscriminate"] = TargetPreference.Indiscriminate,
    };

    static readonly Dictionary<string, BossPhaseKind> PhaseKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["none"] = BossPhaseKind.None, ["breakpoint"] = BossPhaseKind.Breakpoint, ["escalating"] = BossPhaseKind.Escalating,
    };

    static int RungFor(string threatBand, CreatureThreatTuning tuning)
    {
        foreach (var t in tuning.Thresholds)
            if (string.Equals(t.Id, threatBand, StringComparison.Ordinal)) return t.Rung;
        throw new InvalidOperationException($"threatBand '{threatBand}' has no rung in creature-threat.v1.json");
    }

    static EncounterSlot ReadSlot(JsonElement el)
    {
        var posture = Enum.Parse<Posture>(el.GetProperty("posture").GetString()!, ignoreCase: true);
        var reach = Reaches[el.GetProperty("reach").GetString()!];
        var targetPreference = TargetPreferences[el.GetProperty("targetPreference").GetString()!];
        var countBand = el.GetProperty("countBand").GetString()!;
        return new EncounterSlot(posture, reach, targetPreference, countBand);
    }

    public static IReadOnlyList<EncounterAnchor> LoadAll(string encountersDir, CreatureThreatTuning threatTuning)
    {
        if (encountersDir is null) throw new ArgumentNullException(nameof(encountersDir));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (!Directory.Exists(encountersDir)) return Array.Empty<EncounterAnchor>();

        var rows = new List<EncounterAnchor>();
        foreach (var path in Directory.EnumerateFiles(encountersDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;
            rows.Add(ReadEntry(JsonDocument.Parse(File.ReadAllText(path)).RootElement, threatTuning));
        }
        return rows;
    }

    /// <summary>D4.17 row 6's own bridging gap (party-dungeon-todo.md, 2026-09-07): a room's own
    /// `encounterRef` names an `encounterId` string, but <see cref="EncounterAnchor"/> deliberately
    /// carries no id (this file's own class doc comment) — <see cref="LoadAll"/> alone cannot answer
    /// "which anchor does this id mean." Reads the SAME entries through the SAME <see cref="ReadEntry"/>
    /// helper, additionally keyed by the one field <see cref="LoadAll"/> reads and discards.</summary>
    public static IReadOnlyDictionary<string, EncounterAnchor> LoadAllById(string encountersDir, CreatureThreatTuning threatTuning)
    {
        if (encountersDir is null) throw new ArgumentNullException(nameof(encountersDir));
        if (threatTuning is null) throw new ArgumentNullException(nameof(threatTuning));
        if (!Directory.Exists(encountersDir)) return new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal);

        var byId = new Dictionary<string, EncounterAnchor>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(encountersDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            var root = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
            byId[root.GetProperty("encounterId").GetString()!] = ReadEntry(root, threatTuning);
        }
        return byId;
    }

    static EncounterAnchor ReadEntry(JsonElement root, CreatureThreatTuning threatTuning)
    {
        var formation = Enum.Parse<Formation>(root.GetProperty("formation").GetString()!, ignoreCase: true);
        var elementSpread = Enum.Parse<ElementSpreadMode>(root.GetProperty("elementSpread").GetString()!, ignoreCase: true);

        var slots = root.GetProperty("slots").EnumerateArray().Select(ReadSlot).ToList();
        var rankOrder = root.GetProperty("rankOrder").EnumerateArray().Select(e => e.GetInt32()).ToList();

        var tw = root.GetProperty("threatWindow");
        var threatWindow = new ThreatWindow(
            RungFor(tw.GetProperty("floorRung").GetString()!, threatTuning),
            RungFor(tw.GetProperty("ceilRung").GetString()!, threatTuning));

        BossKit? bossKit = null;
        if (root.TryGetProperty("boss", out var bossEl))
        {
            bossKit = new BossKit(
                PatternId: bossEl.GetProperty("build").GetString()!,
                SignatureAction: bossEl.GetProperty("signatureAction").GetString()!,
                PhaseKind: PhaseKinds[bossEl.GetProperty("phasing").GetString()!],
                RetinueSlotIndex: bossEl.GetProperty("retinue").GetInt32());
        }

        return new EncounterAnchor(
            formation, slots, rankOrder, elementSpread, threatWindow, BossSpeciesRef: null, bossKit);
    }
}
