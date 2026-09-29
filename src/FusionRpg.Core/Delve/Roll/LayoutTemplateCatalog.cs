using System.Text.Json;
using FusionRpg.Core.Dungeon.Registry;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Roll;

/// <summary>Named content rules this module raises, under its own registered namespace — the same
/// "one code with a namespaced payload" shape `DomainRules`/`EventRules`/`QuestRules` already
/// establish.</summary>
public static class LayoutRules
{
    public const string Namespace = "layout";

    public const string DuplicateId = "layout.duplicate";
    public const string BadBandMember = "layout.bad-band-member";
    public const string EmptyRaidModes = "layout.empty-raid-modes";
    public const string BadRaidMode = "layout.bad-raid-mode";
    public const string DuplicateRaidMode = "layout.duplicate-raid-mode";

    static LayoutRules() => ContentRuleNamespaces.Register(Namespace);

    public static void EnsureRegistered() { }

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>The load result — a catalog plus every rejection, never a thrown exception
/// (`DomainCatalogLoad`'s own shape): N bad rows report N rejections in one pass, and the catalog
/// holds every GOOD row regardless.</summary>
public readonly record struct LayoutTemplateCatalogLoad(LayoutTemplateCatalog Catalog, IReadOnlyList<AtomRejection> Rejections);

/// <summary>
/// D4.30's real prerequisite chain (2026-09-07): the C# reader `LayoutTemplateCatalog` naming a real
/// gap in D4.19/D4.21 (`RaidModesForLayout`, `DomainOffers`/`DelveStart`'s own honest "no source
/// anywhere" delegate) — closed now that real content exists to build a reader against
/// (`gk-data/packs/fusion/data/seed/dungeon/layouts/*.json`, six entries, `dungeon-layout`'s own 100%-PLANNED corpus).
///
/// <para>Validates every band field against the SAME committed `bands.v1.json` the seedsmith side
/// reads (`BandCatalog`, D1.1/D1.2) — never a private copy of the legal member lists — and
/// `raidModes` against `RaidModeCatalog`'s own real vocabulary, mirroring `DomainCatalog.Load`'s
/// established "caller supplies the registries, this function never reaches into a static hub
/// itself" shape, so this type stays testable with a hand-built registry fixture, not only against
/// whichever hub happens to be configured in the current test run.</para>
/// </summary>
public sealed class LayoutTemplateCatalog
{
    readonly IReadOnlyDictionary<string, LayoutTemplate> _byId;

    LayoutTemplateCatalog(IReadOnlyDictionary<string, LayoutTemplate> byId) => _byId = byId;

    public int Count => _byId.Count;

    public IReadOnlyList<LayoutTemplate> All => _byId.Values.OrderBy(l => l.LayoutId, StringComparer.Ordinal).ToList();

    public LayoutTemplate? Resolve(string layoutId) => _byId.TryGetValue(layoutId, out var row) ? row : null;

    /// <summary>The real implementation `DomainOffers`/`DelveStart`'s own `RaidModesForLayout`
    /// delegate needed — an unknown layout id returns an empty list (never throws), matching
    /// `DelveStart.Run`'s own consuming line (`.Contains(request.RaidMode, ...)`), which already
    /// treats "not offered" and "not found" as the same natural refusal.</summary>
    public IReadOnlyList<string> RaidModesFor(string layoutId) => Resolve(layoutId)?.RaidModes ?? Array.Empty<string>();

    public static LayoutTemplateCatalogLoad Load(
        IReadOnlyList<LayoutTemplate> rows,
        IReadOnlyDictionary<string, BandDef> bands,
        IReadOnlyList<string> raidModeVocabulary)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (bands is null) throw new ArgumentNullException(nameof(bands));
        if (raidModeVocabulary is null) throw new ArgumentNullException(nameof(raidModeVocabulary));

        var fails = new List<AtomRejection>();
        var byId = new Dictionary<string, LayoutTemplate>(StringComparer.Ordinal);

        bool BandHas(string bandName, string value) => bands.TryGetValue(bandName, out var def) && def.Members.Contains(value, StringComparer.Ordinal);

        foreach (var row in rows.OrderBy(r => r.LayoutId, StringComparer.Ordinal))
        {
            if (byId.ContainsKey(row.LayoutId))
            {
                fails.Add(LayoutRules.Fail(LayoutRules.DuplicateId, $"'{row.LayoutId}' is defined twice"));
                continue;
            }

            if (!BandHas("depthBand", row.SizeBand))
            {
                fails.Add(LayoutRules.Fail(LayoutRules.BadBandMember, $"{row.LayoutId}: sizeBand '{row.SizeBand}' is not a known depthBand member"));
                continue;
            }
            if (!BandHas("widthBand", row.WidthBand))
            {
                fails.Add(LayoutRules.Fail(LayoutRules.BadBandMember, $"{row.LayoutId}: widthBand '{row.WidthBand}' is not a known widthBand member"));
                continue;
            }
            if (!BandHas("branchiness", row.Branchiness))
            {
                fails.Add(LayoutRules.Fail(LayoutRules.BadBandMember, $"{row.LayoutId}: branchiness '{row.Branchiness}' is not a known branchiness member"));
                continue;
            }
            if (!BandHas("density", row.GateDensity) || !BandHas("density", row.SecretDensity) || !BandHas("density", row.OneWayDensity))
            {
                fails.Add(LayoutRules.Fail(LayoutRules.BadBandMember, $"{row.LayoutId}: a density field is not a known density member"));
                continue;
            }

            var raidModes = row.RaidModes;
            if (raidModes is null || raidModes.Count == 0)
            {
                fails.Add(LayoutRules.Fail(LayoutRules.EmptyRaidModes, $"{row.LayoutId}: raidModes must never be empty"));
                continue;
            }
            if (raidModes.Distinct(StringComparer.Ordinal).Count() != raidModes.Count)
            {
                fails.Add(LayoutRules.Fail(LayoutRules.DuplicateRaidMode, $"{row.LayoutId}: raidModes has a duplicate entry"));
                continue;
            }
            var badRaidMode = raidModes.FirstOrDefault(m => !raidModeVocabulary.Contains(m, StringComparer.Ordinal));
            if (badRaidMode is not null)
            {
                fails.Add(LayoutRules.Fail(LayoutRules.BadRaidMode, $"{row.LayoutId}: raidMode '{badRaidMode}' is not a known raid mode"));
                continue;
            }

            byId[row.LayoutId] = row;
        }

        return new LayoutTemplateCatalogLoad(new LayoutTemplateCatalog(byId), fails);
    }
}

/// <summary>
/// Reads `gk-data/packs/fusion/data/seed/dungeon/layouts/*.json` into `LayoutTemplate` rows — the same direct-file-read
/// shape `DungeonRegistryLoader.LoadAll` already uses for the nine registry files (Core reads
/// committed SEED content directly; this is not player data, so `guard-dal.ps1`'s SQL-only boundary
/// does not apply). `_index.json` is skipped (it is the directory's own lookup index, not an
/// anchor).
/// </summary>
public static class LayoutSeedFile
{
    public static IReadOnlyList<LayoutTemplate> LoadAll(string layoutsDir)
    {
        if (layoutsDir is null) throw new ArgumentNullException(nameof(layoutsDir));
        if (!Directory.Exists(layoutsDir)) return Array.Empty<LayoutTemplate>();

        var rows = new List<LayoutTemplate>();
        foreach (var path in Directory.EnumerateFiles(layoutsDir, "*.json").OrderBy(p => p, StringComparer.Ordinal))
        {
            if (string.Equals(Path.GetFileName(path), "_index.json", StringComparison.OrdinalIgnoreCase)) continue;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var raidModes = root.GetProperty("raidModes").EnumerateArray().Select(e => e.GetString()!).ToList();
            rows.Add(new LayoutTemplate(
                root.GetProperty("layoutId").GetString()!,
                root.GetProperty("sizeBand").GetString()!,
                root.GetProperty("widthBand").GetString()!,
                root.GetProperty("branchiness").GetString()!,
                root.GetProperty("gateDensity").GetString()!,
                root.GetProperty("secretDensity").GetString()!,
                root.GetProperty("oneWayDensity").GetString()!,
                raidModes));
        }
        return rows;
    }
}

/// <summary>Configure-once-at-startup hub, the exact minimal shape `DungeonTuningHub` already
/// establishes — `Program.cs`/`RpgHost.cs` load the real directory once; every request-time reader
/// (`DelveEndpoints.cs`'s `RaidModesForLayout`) reads the same configured catalog.</summary>
public static class LayoutTemplateHub
{
    static LayoutTemplateCatalog? _catalog;

    public static void Configure(LayoutTemplateCatalog catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public static LayoutTemplateCatalog Catalog => _catalog ?? throw new InvalidOperationException(
        "LayoutTemplateHub.Configure(...) has not run. Layout content lives at " +
        "data/seed/dungeon/layouts/*.json — there is no built-in default to fall back to.");
}
