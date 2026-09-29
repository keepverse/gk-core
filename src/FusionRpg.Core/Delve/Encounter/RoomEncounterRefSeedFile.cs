using System.Text.Json;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// D4.17 row 6's own real bridging gap (party-dungeon-todo.md, 2026-09-07): each real shipped room
/// anchor names exactly one `encounterRef` (`"none"` for a room with no combat — rest/merchant/cache),
/// but <see cref="Roll.RoomPaletteEntry"/> deliberately carries only the three fields
/// <see cref="Roll.DelveGraphRoll"/> itself reads (`RoomId`/`Kind`/`Climate`) — adding an unrelated
/// encounter concern to that record would bloat a hot, already-tested type for one caller. This is the
/// smallest reader that closes the gap instead, mirroring <see cref="Roll.RoomPaletteSeedFile"/>'s own
/// established direct-file-read shape.
/// </summary>
public static class RoomEncounterRefSeedFile
{
    /// <summary>Keyed by `roomId`. The value is `null` exactly for the `"none"` sentinel (no combat in
    /// this room) — the same `NoneToNull` convention <see cref="Domains.DomainSeedFile"/> already
    /// applies to its own nullable string fields.</summary>
    public static IReadOnlyDictionary<string, string?> LoadAll(string roomsDir)
    {
        if (roomsDir is null) throw new ArgumentNullException(nameof(roomsDir));
        if (!Directory.Exists(roomsDir)) return new Dictionary<string, string?>(StringComparer.Ordinal);

        var byId = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(roomsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var roomId = root.GetProperty("roomId").GetString()!;
            var encounterRef = root.GetProperty("encounterRef").GetString()!;
            byId[roomId] = encounterRef == "none" ? null : encounterRef;
        }
        return byId;
    }
}
