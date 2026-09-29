using System.Text.Json;

namespace FusionRpg.Core.Creatures;

public sealed record BannerTuning(int CostPerPull, int CostPerTen, double FocusWeightMultiplier);

/// <summary>
/// Ten-rung roll rates (seed-to-concrete T4.1, owner Q15: "All ten, two pity guards at 70 and 90").
/// Field names carry the RUNG they guard/pay out, not the old four-value names — spec-rarity-
/// migration.md §6: "renamed... so a reader is never guessing which rarity 'epic' means." The two
/// pity guards are ordinals from ssot-rarity.md §3.3: 70 = Heirloom, 90 = Sunwoven. `Almanac` (the
/// true top, ordinal 100) sits above both guards and is never pity-boosted — Q15 names guards at
/// 70/90 only. `Chaff` has no explicit rate: it is the roll's remainder, same shape the old
/// `Common` band used.
/// </summary>
public sealed record RollerTuning(
    int HeirloomHardPity, int SunwovenSoftStart, int SunwovenHardPity, int SunwovenBasePerMille,
    int SunwovenRampPerMille, int AlmanacPerMille, int HeirloomPerMille, int FirstseedPerMille,
    int ChimericPerMille, int FusedPerMille, int CultivatedPerMille, int GraftedPerMille,
    int SproutPerMille, int ShinyOneIn);

/// <summary>Summoning balance surface (tunables-ssot.md T1) — spec-creature-summoning.md. Banner
/// ids/HasElementFocus stay in <see cref="SummonBannerCatalog"/> (schema); costs/pity/rarity moved.</summary>
public sealed record SummoningTuning(
    int SchemaVersion, int Version,
    IReadOnlyDictionary<string, BannerTuning> Banners, RollerTuning Roller);

public sealed class SummoningTuningRejection : Exception
{
    public SummoningTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class SummoningTuningLoader
{
    public static SummoningTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SummoningTuningRejection("summoning tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SummoningTuningRejection($"summoning tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var bannersEl = Obj(root, "banners");
            var rollerEl = Obj(root, "roller");

            var banners = new Dictionary<string, BannerTuning>(StringComparer.Ordinal);
            foreach (var prop in bannersEl.EnumerateObject())
            {
                var b = prop.Value;
                banners[prop.Name] = new BannerTuning(
                    CostPerPull: Int(b, "costPerPull", prop.Name),
                    CostPerTen: Int(b, "costPerTen", prop.Name),
                    FocusWeightMultiplier: Double(b, "focusWeightMultiplier", prop.Name));
            }

            var roller = new RollerTuning(
                HeirloomHardPity: Int(rollerEl, "heirloomHardPity", "roller"),
                SunwovenSoftStart: Int(rollerEl, "sunwovenSoftStart", "roller"),
                SunwovenHardPity: Int(rollerEl, "sunwovenHardPity", "roller"),
                SunwovenBasePerMille: Int(rollerEl, "sunwovenBasePerMille", "roller"),
                SunwovenRampPerMille: Int(rollerEl, "sunwovenRampPerMille", "roller"),
                AlmanacPerMille: Int(rollerEl, "almanacPerMille", "roller"),
                HeirloomPerMille: Int(rollerEl, "heirloomPerMille", "roller"),
                FirstseedPerMille: Int(rollerEl, "firstseedPerMille", "roller"),
                ChimericPerMille: Int(rollerEl, "chimericPerMille", "roller"),
                FusedPerMille: Int(rollerEl, "fusedPerMille", "roller"),
                CultivatedPerMille: Int(rollerEl, "cultivatedPerMille", "roller"),
                GraftedPerMille: Int(rollerEl, "graftedPerMille", "roller"),
                SproutPerMille: Int(rollerEl, "sproutPerMille", "roller"),
                ShinyOneIn: Int(rollerEl, "shinyOneIn", "roller"));

            return new SummoningTuning(
                SchemaVersion: Int(root, "schemaVersion", "$"),
                Version: Int(root, "version", "$"),
                Banners: banners, Roller: roller);
        }
    }

    static JsonElement Obj(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new SummoningTuningRejection($"summoning tuning: missing or non-object '{key}'");
        return el;
    }

    static int Int(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new SummoningTuningRejection($"summoning tuning: missing or non-integer '{path}.{key}'");
        return v;
    }

    static double Double(JsonElement parent, string key, string path)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new SummoningTuningRejection($"summoning tuning: missing or non-number '{path}.{key}'");
        return el.GetDouble();
    }
}

/// <summary>Fans one summoning.v{n}.json load out to both classes that read it (tunables-ssot.md §7.2).</summary>
public static class SummoningTuningHub
{
    public static void Configure(SummoningTuning tuning)
    {
        SummonBannerCatalog.Configure(tuning);
        SummonRoller.Configure(tuning);
    }
}
