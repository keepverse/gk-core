using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// Pure parser for <c>data/seed/items/_tuning/tier-bands.v{n}.json</c> — the one genuinely tunable
/// surface a family's magnitude is computed from (that file's own <c>_meta.note</c>). Not
/// <c>bands.v1.json</c> (frozen, registry-side, never authored per-channel data) — the two are
/// different files with different owners; this parser reads only the tunable one.
/// </summary>
public static class TierBandsFile
{
    static readonly Regex VersionPattern = new(@"^tier-bands\.v(\d+)\.json$", RegexOptions.Compiled);

    /// <summary>
    /// Real bug fix, 2026-09-08 (atom-family-expansion, `tier-bands-coverage`): every caller of this
    /// class previously hardcoded literal <c>"tier-bands.v1.json"</c>, so
    /// <c>seedsmith numerics rebalance --publish</c> (which writes <c>tier-bands.v{n+1}.json</c>, the
    /// old version staying for revert — exactly as its own <c>_meta.rebalance</c> line promises) had
    /// zero effect on the real generator: two real, non-trivial publishes (<c>v2.json</c>/
    /// <c>v3.json</c>) already existed on disk, unread by anything. Mirrors
    /// <c>seedsmith.numerics.tier_bands_io.load("latest")</c>'s own glob-and-pick-highest-version-
    /// number logic exactly, so both languages resolve "latest" identically. Numeric comparison, not
    /// lexicographic — <c>v10</c> must beat <c>v2</c>.
    /// </summary>
    public static string FindLatestPath(string tuningDir)
    {
        var best = Directory.EnumerateFiles(tuningDir, "tier-bands.v*.json")
            .Select(p => (Path: p, Match: VersionPattern.Match(Path.GetFileName(p))))
            .Where(t => t.Match.Success)
            .Select(t => (t.Path, Version: int.Parse(t.Match.Groups[1].Value)))
            .OrderByDescending(t => t.Version)
            .FirstOrDefault();

        if (best.Path is null)
            throw new FileNotFoundException($"no tier-bands.v*.json under {tuningDir}");

        return best.Path;
    }

    public static TierBandsInput Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("baseSharePermille", out var baseShareEl) || baseShareEl.ValueKind != JsonValueKind.Number)
            throw new FormatException("tier-bands: missing integer 'baseSharePermille'");

        if (!root.TryGetProperty("channelWeightPermille", out var channelWeightEl) || channelWeightEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("tier-bands: missing object 'channelWeightPermille'");

        if (!root.TryGetProperty("opWeightPermille", out var opWeightEl) || opWeightEl.ValueKind != JsonValueKind.Object)
            throw new FormatException("tier-bands: missing object 'opWeightPermille'");

        return new TierBandsInput(baseShareEl.GetInt64(), ReadMap(channelWeightEl), ReadMap(opWeightEl));
    }

    static IReadOnlyDictionary<string, long> ReadMap(JsonElement obj)
    {
        var d = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
            d[p.Name] = p.Value.GetInt64();
        return d;
    }
}
