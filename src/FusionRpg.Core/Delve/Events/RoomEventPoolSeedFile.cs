using System.Text.Json;

namespace FusionRpg.Core.Delve.Events;

/// <summary>
/// `event-deck` D3.9's own real bridging gap (party-dungeon-todo.md, 2026-09-07, re-discovered while
/// investigating `domain-catalog`'s D4.17 row 7): the spec's own vocabulary calls a room anchor an
/// "archetype" (spec-event-deck.md §9: "the archetype's `eventPool`"; §1: "Deck per domain = the union
/// of `eventPool` over the archetypes in the domain's `roomPalette`") — each real shipped room anchor
/// carries its own `eventPool: string[]` directly, but no existing reader exposes it keyed by room id.
/// `Roll.RoomPaletteEntry` deliberately carries only the three fields `DelveGraphRoll` itself reads
/// (`RoomId`/`Kind`/`Climate`) — this is the smallest reader that closes the gap instead, mirroring
/// `Delve.Encounter.RoomEncounterRefSeedFile`'s own identical shape for the sibling `encounterRef`
/// field, built the same day for `domain-catalog`'s row 6.
/// </summary>
public static class RoomEventPoolSeedFile
{
    /// <summary>Keyed by `roomId`. An empty list is a real, legal value (a room with no authored
    /// event pool, e.g. most `fight`/`boss` kinds today) — never `null`, since the field is always
    /// present on a real shipped anchor.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> LoadAll(string roomsDir)
    {
        if (roomsDir is null) throw new ArgumentNullException(nameof(roomsDir));
        if (!Directory.Exists(roomsDir)) return new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        var byId = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(roomsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var roomId = root.GetProperty("roomId").GetString()!;
            var pool = root.GetProperty("eventPool").EnumerateArray().Select(e => e.GetString()!).ToList();
            byId[roomId] = pool;
        }
        return byId;
    }
}
