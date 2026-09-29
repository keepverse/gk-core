namespace FusionRpg.Core.Dungeon.Registry;

/// <summary>One ordinal band vocabulary — a member list plus a display name per member
/// (added by <c>delve-stage</c>, wave 5: the composed-band names and the three nerve stages, so the
/// player-facing stage never receives the ordinal a band came from). A registry row never carries a
/// magnitude — the numeric mapping per member (a hunger ‰, a row count) lives in <c>dungeon.v1.json</c>'s
/// <c>bands.&lt;band&gt;.&lt;member&gt;</c> tuning keys, joined by whoever reads the band.</summary>
public sealed record BandDef
{
    public string BandName { get; init; } = "";
    public IReadOnlyList<string> Members { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, string> DisplayNames { get; init; } = new Dictionary<string, string>();
}

public static class BandCatalog
{
    const string File = "bands.v1.json";

    /// <summary>The closed list of band vocabularies this registry owns — every ordinal an anchor
    /// writes (spec-dungeon-registries.md "bands.v1.json" row). Exactly these twenty, no more, no
    /// fewer: a missing or an extra band name is G20 with a different noun.</summary>
    public static readonly IReadOnlyList<string> BandNames = new[]
    {
        "dangerBand", "depthBand", "widthBand", "branchiness", "density", "hazardBand", "sightBand",
        "countBand", "elementSpread", "formation", "eventKind", "outcomeOrdinal", "repeatScope",
        "entry", "phasing", "questScope", "rewardBand", "deltaBand", "hpBand", "nerveStage"
    };

    static IReadOnlyDictionary<string, BandDef>? _all;

    public static IReadOnlyDictionary<string, BandDef> All => _all
        ?? throw new InvalidOperationException("BandCatalog.Configure(...) has not run.");

    public static bool IsKnownBand(string? bandName) => bandName != null && All.ContainsKey(bandName);

    public static BandDef Get(string bandName) =>
        All.TryGetValue(bandName, out var def) ? def : throw new ArgumentException($"Unknown band '{bandName}'.");

    public static bool IsKnownMember(string bandName, string? member) =>
        member != null && Get(bandName).Members.Contains(member, StringComparer.Ordinal);

    public static void Configure(IReadOnlyDictionary<string, BandDef> bands) => _all = Validate(bands);

    public static IReadOnlyDictionary<string, BandDef> Validate(IReadOnlyDictionary<string, BandDef> bands)
    {
        var missing = BandNames.Where(n => !bands.ContainsKey(n)).ToList();
        if (missing.Count > 0)
            throw new DungeonRegistryRejection($"{File}: missing band(s) {string.Join(", ", missing)}.");
        var extra = bands.Keys.Where(n => !BandNames.Contains(n)).ToList();
        if (extra.Count > 0)
            throw new DungeonRegistryRejection($"{File}: unknown band(s) {string.Join(", ", extra)} — not one of the twenty owned here.");

        foreach (var (bandName, def) in bands)
        {
            if (def.Members.Count == 0)
                throw new DungeonRegistryRejection($"{File}: band '{bandName}' has no members.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in def.Members)
            {
                DungeonRegistryIds.RequireKebab(m, $"Band '{bandName}' member", File);
                if (!seen.Add(m))
                    throw new DungeonRegistryRejection($"{File}: band '{bandName}' has duplicate member '{m}'.");
                DungeonRegistryIds.RejectSpelledNumber(m, bandName, File);
                if (!def.DisplayNames.ContainsKey(m))
                    throw new DungeonRegistryRejection($"{File}: band '{bandName}' member '{m}' has no display name.");
            }

            var strayNames = def.DisplayNames.Keys.Where(k => !seen.Contains(k)).ToList();
            if (strayNames.Count > 0)
                throw new DungeonRegistryRejection(
                    $"{File}: band '{bandName}' has a display name for non-member(s) {string.Join(", ", strayNames)}.");
        }

        return bands;
    }

    public static IReadOnlyDictionary<string, BandDef> Parse(string json)
    {
        var root = DungeonRegistryJson.Root(json, File);
        var bandsEl = DungeonRegistryJson.Obj(root, "bands", "$", File);
        var result = new Dictionary<string, BandDef>(StringComparer.Ordinal);
        foreach (var prop in bandsEl.EnumerateObject())
        {
            var bandName = prop.Name;
            var el = prop.Value;
            var members = DungeonRegistryJson.StringArray(el, "members", $"bands.{bandName}", File);
            var namesEl = DungeonRegistryJson.Obj(el, "displayNames", $"bands.{bandName}", File);
            var displayNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var nameProp in namesEl.EnumerateObject())
                displayNames[nameProp.Name] = DungeonRegistryJson.Str(namesEl, nameProp.Name, $"bands.{bandName}.displayNames", File);

            result[bandName] = new BandDef { BandName = bandName, Members = members, DisplayNames = displayNames };
        }
        return result;
    }
}
