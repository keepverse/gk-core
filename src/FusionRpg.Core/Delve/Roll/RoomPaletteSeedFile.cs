using System.Text.Json;
using FusionRpg.Core.Delve.Domains;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Roll;

/// <summary>
/// D4.17 row 4's own real bridging gap (party-dungeon-todo.md, 2026-09-07): <see cref="DelveGraphRoll.Roll"/>
/// needs a <see cref="DomainAnchor"/>, whose own <see cref="RoomPaletteEntry"/> list needs each real
/// room's `kind`/`climate` — fields no existing reader exposes (room's own C# consumer, per D1.10's
/// own scope-down, is Python-side only). This is the smallest reader that closes that gap: exactly
/// the three fields <see cref="RoomPaletteEntry"/> names, read directly from
/// `gk-data/packs/fusion/data/seed/dungeon/rooms/*.json`, mirroring <see cref="LayoutSeedFile"/>'s own established
/// direct-file-read shape rather than inventing a fuller room anchor type nothing else needs today.
/// </summary>
public static class RoomPaletteSeedFile
{
    /// <summary>Keyed by `roomId` — the shape <see cref="DomainAnchorBuilder"/> looks each
    /// `roomPalette` entry up by. `_index.json` is skipped (the directory's own lookup index, not an
    /// anchor) — the same convention <see cref="LayoutSeedFile"/> already applies.</summary>
    public static IReadOnlyDictionary<string, RoomPaletteEntry> LoadAll(string roomsDir)
    {
        if (roomsDir is null) throw new ArgumentNullException(nameof(roomsDir));
        if (!Directory.Exists(roomsDir)) return new Dictionary<string, RoomPaletteEntry>(StringComparer.Ordinal);

        var byId = new Dictionary<string, RoomPaletteEntry>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(roomsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var roomId = root.GetProperty("roomId").GetString()!;
            var climateStr = root.GetProperty("climate").GetString()!;

            ElementTypeId? climate = null;
            if (climateStr != "none")
            {
                if (!ElementRoster.TryParse(climateStr, out var parsed))
                    throw new NotSupportedException($"{path}: unknown climate '{climateStr}' — not 'none' and not a real element id");
                climate = parsed;
            }

            byId[roomId] = new RoomPaletteEntry(roomId, root.GetProperty("kind").GetString()!, climate);
        }
        return byId;
    }
}

/// <summary>
/// Builds a <see cref="DomainAnchor"/> from D4.15's own catalog projection
/// (<see cref="DomainRow"/>) plus a real room lookup — the bridge D4.17 row 4 named as
/// missing. Deliberately a pure function over caller-supplied data (no file I/O, no store), matching
/// every other Core-layer builder in this program; a caller (the eventual `DomainPreflight.Run`
/// wiring) owns loading the room lookup once and reusing it across every domain.
/// </summary>
public static class DomainAnchorBuilder
{
    /// <summary>Throws <see cref="KeyNotFoundException"/> naming the missing room id rather than
    /// silently dropping it from the palette — a `roomPalette` entry that resolves to nothing is a
    /// real content defect (the id was never real, or the room corpus regressed), not something a
    /// graph roll should quietly under-fill against.</summary>
    public static DomainAnchor From(DomainRow domain, IReadOnlyDictionary<string, RoomPaletteEntry> roomsById, IReadOnlyList<string> roomPalette)
    {
        if (domain is null) throw new ArgumentNullException(nameof(domain));
        if (roomsById is null) throw new ArgumentNullException(nameof(roomsById));
        if (roomPalette is null) throw new ArgumentNullException(nameof(roomPalette));

        if (!ElementRoster.TryParse(domain.Climate, out var climate))
            throw new NotSupportedException($"domain '{domain.DomainId}': unknown climate '{domain.Climate}' — not a real element id");

        var entries = new List<RoomPaletteEntry>(roomPalette.Count);
        foreach (var roomId in roomPalette)
        {
            if (!roomsById.TryGetValue(roomId, out var entry))
                throw new KeyNotFoundException($"domain '{domain.DomainId}': roomPalette names '{roomId}', which is not a real room");
            entries.Add(entry);
        }

        return new DomainAnchor(domain.DomainId, climate, domain.DangerBand, entries);
    }
}
