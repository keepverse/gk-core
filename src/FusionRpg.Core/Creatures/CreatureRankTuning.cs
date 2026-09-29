using System.Text.Json;

namespace FusionRpg.Core.Creatures;

/// <summary>One cell of the <c>threatBand × rarity → rank</c> grid
/// (<c>gk-core/data/tuning/creature-rank.v1.json</c>, spec-species-rank.md §2). Threat selects the ROW,
/// rarity selects the COLUMN, the cell holds the rank — the two axes are never conflated, and the
/// cell never holds a magnitude.</summary>
public sealed record CreatureRankCell(string ThreatBand, string Rarity, string RankId);

public sealed class CreatureRankTuningRejection : Exception
{
    public CreatureRankTuningRejection(string message) : base(message) { }
}

/// <summary>
/// The parsed <c>gk-core/data/tuning/creature-rank.v1.json</c>: a closed 10-id rank vocabulary, the 10×10
/// <c>threatBand × rarity</c> grid, and the five per-gate floors (spec-species-rank.md §2, §6).
///
/// <para><b>Rank ids stay strings here on purpose.</b> The C# <see cref="CreatureRank"/> enum is the
/// runtime vocabulary (Task 2); this record is the file's own closed vocabulary validated at load, so
/// a file edit that renames or drops a rank id fails against the <c>ranks</c> table rather than
/// silently parsing into a default enum member.</para>
///
/// <para><b>Unresolved never reaches the table.</b> <c>"unresolved"</c> is the classification
/// pipeline's own sentinel for an input that never resolved (spec Assumption 4). Rank is skipped —
/// never fabricated — for such an input, so neither the grid nor <see cref="RankIdFor"/> will accept
/// it: an unresolved row is the caller's to skip, and asking the table for one is a bug, not a
/// default. The <c>null</c> mapping happens at the C# boundary, never here.</para>
/// </summary>
public sealed record CreatureRankTuning(
    int Version,
    IReadOnlyList<string> RankIds,
    IReadOnlyList<CreatureRankCell> Grid,
    IReadOnlyDictionary<string, string> Floors)
{
    /// <summary>The classification pipeline's unresolved sentinel — a genuine vote that never
    /// converged. Literal here to match <c>SpeciesExpander</c>'s own read of the same string, never a
    /// second vocabulary.</summary>
    public const string UnresolvedId = "unresolved";

    /// <summary>The five gate floors the table carries (spec §6), in spec order. A closed
    /// vocabulary the code owns — a sixth gate is a reviewed change to this list and the file
    /// together, never a silently absent floor at a gate.</summary>
    public static IReadOnlyList<string> GateIds { get; } = new[]
    {
        "fusionPromotion", "fusionRecipeEligibility", "expeditionWildBand", "waveBand", "cageEligibility",
    };

    /// <summary>The bottom rung (Chaff) — the pass-through default every gate floor ships at, and the
    /// rank a <c>null</c> rank maps to AT EACH GATE (spec §6), never a fabricated table lookup.</summary>
    public string BottomRankId => RankIds[0];

    /// <summary>The rank id for a resolved <c>(threatBand, rarity)</c> pair. Both inputs must be
    /// resolved: an unresolved or unknown id is refused by name rather than defaulted, because a
    /// fabricated rank is exactly what Assumption 4 forbids.</summary>
    public string RankIdFor(string threatBand, string rarity)
    {
        RefuseUnresolved(threatBand, nameof(threatBand));
        RefuseUnresolved(rarity, nameof(rarity));

        var cell = Grid.FirstOrDefault(c =>
            string.Equals(c.ThreatBand, threatBand, StringComparison.Ordinal) &&
            string.Equals(c.Rarity, rarity, StringComparison.Ordinal));

        return cell?.RankId
            ?? throw new CreatureRankTuningRejection(
                $"creature rank: grid has no cell for (threatBand '{threatBand}', rarity '{rarity}') — " +
                "the loader validated full coverage, so this pair is outside the closed vocabularies.");
    }

    /// <summary>The floor for a gate, by its <see cref="GateIds"/> name.</summary>
    public string FloorFor(string gateId) =>
        Floors.TryGetValue(gateId, out var floor)
            ? floor
            : throw new CreatureRankTuningRejection(
                $"creature rank: no floor for gate '{gateId}' — the closed gate vocabulary is " +
                string.Join(", ", GateIds) + ".");

    static void RefuseUnresolved(string id, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new CreatureRankTuningRejection($"creature rank: {parameterName} is empty — the table is never asked for a missing input.");
        if (string.Equals(id, UnresolvedId, StringComparison.OrdinalIgnoreCase))
            throw new CreatureRankTuningRejection(
                $"creature rank: {parameterName} is the unresolved sentinel — rank is skipped for an unresolved input " +
                "(spec §1, Assumption 4); it is never defaulted through the table.");
    }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2). The two axis vocabularies are READ from
/// their declaring files and passed in — <c>CreatureThreatTuning.RungIds</c> from
/// <c>creature-threat.v1.json</c> and the rarity ladder's ids from <c>gk-data/packs/fusion/data/seed/rarity/ladder.v1.json</c>
/// (via the <see cref="CreatureRarity"/> declaring enum, tier-propagation-contract T-2) — never
/// restated here.</summary>
public static class CreatureRankTuningLoader
{
    public const string File = "creature-rank.v1.json";

    public static CreatureRankTuning Parse(
        string json, IReadOnlyList<string> threatBandIds, IReadOnlyList<string> rarityIds)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CreatureRankTuningRejection($"creature rank: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new CreatureRankTuningRejection("creature rank: expected a top-level object");

            var version = Int(root, "version", "$");
            var ranks = StrArray(root, "ranks", "$");

            if (ranks.Count == 0)
                throw new CreatureRankTuningRejection("creature rank: the 'ranks' table is empty");
            if (ranks.Distinct(StringComparer.Ordinal).Count() != ranks.Count)
                throw new CreatureRankTuningRejection("creature rank: the 'ranks' table repeats an id");

            // The rank vocabulary mirrors the rarity ladder 1:1, row for row (spec Assumption 1,
            // owner-confirmed). A drift between the file's own table and the declaring ladder is a
            // load failure, not a silent second vocabulary.
            if (!ranks.SequenceEqual(rarityIds, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    "creature rank: the 'ranks' table does not mirror the rarity ladder row for row — " +
                    $"table [{string.Join(", ", ranks)}] vs ladder [{string.Join(", ", rarityIds)}]");

            var grid = ReadGrid(root, ranks, threatBandIds, rarityIds);
            var floors = ReadFloors(root, ranks);

            return new CreatureRankTuning(version, ranks, grid, floors);
        }
    }

    static IReadOnlyList<CreatureRankCell> ReadGrid(
        JsonElement root, IReadOnlyList<string> ranks,
        IReadOnlyList<string> threatBandIds, IReadOnlyList<string> rarityIds)
    {
        if (!root.TryGetProperty("grid", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new CreatureRankTuningRejection("creature rank: missing or non-array 'grid'");

        var grid = new List<CreatureRankCell>();
        var seen = new HashSet<(string, string)>();
        var index = -1;
        foreach (var el in arr.EnumerateArray())
        {
            index++;
            var threatBand = Str(el, "threatBand", CellLabel(index));
            var rarity = Str(el, "rarity", CellLabel(index));
            var rank = Str(el, "rank", CellLabel(index));
            var label = CellLabel(index, threatBand, rarity);

            // The sentinel is refused before the vocabulary checks so the message names the real
            // cause (an unresolved input asked for a rank) rather than "unknown id".
            if (IsUnresolved(threatBand) || IsUnresolved(rarity))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} names the unresolved sentinel — unresolved inputs never reach the table " +
                    "(spec §1, Assumption 4); rank is skipped, never fabricated.");
            if (IsUnresolved(rank))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} holds the unresolved sentinel in the 'ranks' table's own column — " +
                    "every cell must hold a real rank id.");

            if (!threatBandIds.Contains(threatBand, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} names threatBand '{threatBand}', which is not in creature-threat.v1.json's rung table.");
            if (!rarityIds.Contains(rarity, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} names rarity '{rarity}', which is not in the rarity ladder table.");
            if (!ranks.Contains(rank, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} names rank '{rank}', which is not in the table's own 'ranks' vocabulary " +
                    $"[{string.Join(", ", ranks)}].");

            if (!seen.Add((threatBand, rarity)))
                throw new CreatureRankTuningRejection(
                    $"creature rank: {label} repeats a (threatBand, rarity) pair already in the grid");

            grid.Add(new CreatureRankCell(threatBand, rarity, rank));
        }

        // Full coverage, expressed over the two declaring vocabularies — never a literal cell count.
        var expected = threatBandIds.Count * rarityIds.Count;
        if (grid.Count != expected)
            throw new CreatureRankTuningRejection(
                $"creature rank: grid holds {grid.Count} cells, expected one per (threatBand, rarity) pair " +
                $"({threatBandIds.Count} × {rarityIds.Count} = {expected})");

        return grid;
    }

    static IReadOnlyDictionary<string, string> ReadFloors(JsonElement root, IReadOnlyList<string> ranks)
    {
        if (!root.TryGetProperty("floors", out var el) || el.ValueKind != JsonValueKind.Object)
            throw new CreatureRankTuningRejection("creature rank: missing or non-object 'floors'");

        var floors = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in el.EnumerateObject())
        {
            if (!CreatureRankTuning.GateIds.Contains(property.Name, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    $"creature rank: floors names gate '{property.Name}', which is not one of the five gates " +
                    $"[{string.Join(", ", CreatureRankTuning.GateIds)}].");

            var rank = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            if (rank is null || !ranks.Contains(rank, StringComparer.Ordinal))
                throw new CreatureRankTuningRejection(
                    $"creature rank: floors.{property.Name} names rank '{rank}', which is not in the table's own " +
                    $"'ranks' vocabulary [{string.Join(", ", ranks)}].");

            floors[property.Name] = rank;
        }

        foreach (var gate in CreatureRankTuning.GateIds)
            if (!floors.ContainsKey(gate))
                throw new CreatureRankTuningRejection(
                    $"creature rank: floors is missing gate '{gate}' — a gate with no row would be a silent no-op.");

        return floors;
    }

    static bool IsUnresolved(string id) =>
        string.Equals(id, CreatureRankTuning.UnresolvedId, StringComparison.OrdinalIgnoreCase);

    static string CellLabel(int index, string? threatBand = null, string? rarity = null) =>
        threatBand is null || rarity is null
            ? $"grid[{index}]"
            : $"grid[{index}] (threatBand '{threatBand}', rarity '{rarity}')";

    static int Int(JsonElement el, string key, string path) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)
            ? n : throw new CreatureRankTuningRejection($"creature rank: missing or non-integer '{key}' at {path}");

    static string Str(JsonElement el, string key, string path) =>
        el.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
            ? s : throw new CreatureRankTuningRejection($"creature rank: missing or non-string '{key}' at {path}");

    static IReadOnlyList<string> StrArray(JsonElement el, string key, string path)
    {
        if (!el.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new CreatureRankTuningRejection($"creature rank: missing or non-array '{key}' at {path}");
        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 } s)
                throw new CreatureRankTuningRejection($"creature rank: '{key}' at {path} holds a non-string entry");
            list.Add(s);
        }
        return list;
    }
}
