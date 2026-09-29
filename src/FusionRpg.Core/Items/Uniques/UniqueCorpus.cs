using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Uniques;

/// <summary>One authored identity atom: a family and a <b>band</b>, never a number (seed-contract §3).</summary>
public readonly record struct UniqueFixedAtomSeed(string Family, string PowerBand);

/// <summary>
/// The one variance slot. <c>Variance</c> is a `bands.v1.json variance` band (narrow/normal/wide) —
/// the ± width around the tier midpoint, not a percentage and not a tier.
/// </summary>
public readonly record struct UniqueVarianceSeed(string Family, string Variance);

/// <summary>
/// The authored counter-pressure declaration. `core.v1.json counterPressure` (added wave 0c
/// <i>because</i> ssot-uniques.md needed it) makes all three kinds authorable without a number:
/// <c>drawback</c> carries a severity band plus the family or channel it costs you, and
/// <c>conditional</c> carries one id from the closed condition list, each of which maps to a predicate
/// leaf the atom layer already ships.
/// </summary>
public sealed record UniqueCounterPressureSeed(
    UniqueCounterPressure Kind, string? SeverityBand, string? Condition, string? Family, string? Channel);

/// <summary>
/// One row of `gk-data/packs/fusion/data/seed/items/uniques/*.json` — the authored <b>seed</b>, not the shipped container.
/// Per the seed-to-concrete rule the corpus emits bands and families; a runtime generator rolls the
/// concrete atoms. This module validates the seed and prices it; it does not roll it.
/// </summary>
/// <param name="Enabled">D4.23 (spec-unique-pipeline.md §5 point 5, verbatim: "The 95 are
/// `enabled: false`; `rungFloorOrdinal = 80` re-seeds `unique_eligible` with no code change") — a
/// retired anchor, never re-runged, never deleted. Defaults `true` so the 49 shipped rows that never
/// set the JSON field, and every hand-built test fixture predating this field, keep their old meaning.</param>
/// <param name="FlavourText">
/// The authored sentence itself, as the seed's sibling <c>flavor</c> field ships it — 112 of the 144
/// shipped rows carry one, and until 2026-09-06 no C# anywhere read it. It is deliberately <b>not</b>
/// written to <c>item_unique</c>: that column is a KEY, never a literal (`RpgStore.ItemUniques.cs:46`),
/// and the literal's home is N2's string catalog (`gk-content/content/display/en.json`), generated from this field
/// rather than hand-authored a second time. Read here so the catalog, the validator and the tests can
/// see the key and its sentence together.
/// </param>
public sealed record UniqueSeed(
    string SeedId,
    string ContainerId,
    string NameKey,
    string Name,
    ItemFrame Frame,
    string BaseTypeId,
    string RarityId,
    string PowerAxis,
    IReadOnlyList<UniqueFixedAtomSeed> FixedAtoms,
    UniqueVarianceSeed? VarianceSlot,
    UniqueCounterPressureSeed CounterPressure,
    UniqueAcquisition Acquisition,
    string? FlavourKey,
    string? FlavourText,
    string Partition,
    string RungBand,
    bool Enabled = true)
{
    /// <summary>
    /// <c>PrefixRolls + SuffixRolls</c> as the seed declares it: the variance slot is the one draw, so
    /// this is 0 or 1 and never reads a <c>pool_rolls</c> column — that column no longer exists.
    /// </summary>
    public int TotalRolls => VarianceSlot is null ? 0 : 1;
}

public sealed class UniqueCorpusRejection : Exception
{
    public AtomRejection Rejection { get; }

    public UniqueCorpusRejection(string ruleId, string detail) : base($"{ruleId}: {detail}")
    {
        UniqueRules.EnsureRegistered();
        Rejection = AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// This module's content-rule namespace. item-ideal.md §2b.1 and README #3: <b>one</b>
/// <see cref="AtomRejectionReason.ContentRuleViolated"/> code carrying a namespaced rule id, never a
/// new member of the closed enum. spec-uniques.md's §12-equivalent wanted seven new codes; the closed
/// list stays at 35 and every one of the eight rules below is a <c>unique.*</c> rule id instead.
/// </summary>
public static class UniqueRules
{
    public const string Namespace = "unique";

    public const string CounterPressure = "unique.counter-pressure";
    public const string Budget = "unique.budget";
    public const string AxisCollision = "unique.axis-collision";
    public const string RoleForbidden = "unique.role-forbidden";
    public const string RungIneligible = "unique.rung-ineligible";
    public const string SetMembership = "unique.set-membership";
    public const string Unreachable = "unique.unreachable";
    public const string Shape = "unique.shape";
    public const string CorpusMalformed = "unique.corpus-malformed";

    static UniqueRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor from a call site that has no other reason to touch it.</summary>
    public static void EnsureRegistered() { }
}

/// <summary>
/// `gk-data/packs/fusion/data/seed/items/uniques/*.json`, parsed. Pure — the caller supplies the JSON text; Core never
/// opens a file.
///
/// <para>The <b>container id is derived</b> (<see cref="UniqueContainerIds"/>), never authored: the
/// corpus's <c>unique.</c> tracking id is not a legal container id at all, and `naming.v1.json`'s own
/// note left the derivation open. An authored second id would be a second source of truth for the same
/// row, which is the defect the derived set-tier ids already avoid.</para>
/// </summary>
public static class UniqueCorpus
{
    public static IReadOnlyList<UniqueSeed> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed, "empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed, $"not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed, "no 'entries' array");

            var partition = "";
            if (root.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
                meta.TryGetProperty("partition", out var pEl) && pEl.ValueKind == JsonValueKind.String)
                partition = pEl.GetString()!;

            // The rung BAND is the partition's own key -- `uniques/{theme}/{rungBandLowOrdinal}` --
            // and it is the third key of the anti-convergence rule. Deriving it from the entry's rung
            // instead would split each band's 40 slots into two half-bands and silently double the
            // grid, which is the opposite of what "exactly saturated at 144" means.
            var band = partition.Split('/').LastOrDefault() ?? "";

            var result = new List<UniqueSeed>();
            foreach (var e in entries.EnumerateArray()) result.Add(ReadEntry(e, partition, band));
            return result;
        }
    }

    static UniqueSeed ReadEntry(JsonElement e, string partition, string band)
    {
        var seedId = Str(e, "id");
        string containerId;
        try { containerId = UniqueContainerIds.FromSeedId(seedId); }
        catch (ArgumentException ex)
        {
            throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed, ex.Message);
        }

        var frameId = Str(e, "frame");
        var frame = frameId switch
        {
            "humanoid" => ItemFrame.Humanoid,
            "plant" => ItemFrame.Plant,
            _ => throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed,
                $"unique '{seedId}' names frame '{frameId}'; a unique occupies one base type, and a base " +
                "type sits on one of the two pure ladders — 'hybrid' is a body, not a ladder"),
        };

        var fixedAtoms = new List<UniqueFixedAtomSeed>();
        if (e.TryGetProperty("fixedAtoms", out var faEl) && faEl.ValueKind == JsonValueKind.Array)
            foreach (var a in faEl.EnumerateArray())
                fixedAtoms.Add(new UniqueFixedAtomSeed(Str(a, "family"), Str(a, "powerBand")));

        if (fixedAtoms.Count == 0)
            throw new UniqueCorpusRejection(UniqueRules.Shape,
                $"unique '{seedId}' authors no identity atom, so nothing about it is authored — it is a " +
                "rare with a name (ssot-uniques.md §3.2)");

        UniqueVarianceSeed? variance = null;
        if (e.TryGetProperty("varianceSlot", out var vEl) && vEl.ValueKind == JsonValueKind.Object)
            variance = new UniqueVarianceSeed(Str(vEl, "family"), Str(vEl, "variance"));

        if (!e.TryGetProperty("counterPressure", out var cpEl) || cpEl.ValueKind != JsonValueKind.Object)
            throw new UniqueCorpusRejection(UniqueRules.CounterPressure,
                $"unique '{seedId}' declares no counterPressure; a unique satisfying none is refused, which " +
                "is what stops the class becoming a strictly-better tier");

        var kindId = Str(cpEl, "kind");
        var kind = kindId switch
        {
            "drawback" => UniqueCounterPressure.Drawback,
            "conditional" => UniqueCounterPressure.Conditional,
            "narrow" => UniqueCounterPressure.Narrow,
            _ => throw new UniqueCorpusRejection(UniqueRules.CounterPressure,
                $"unique '{seedId}' declares counterPressure kind '{kindId}', which is not one of " +
                "core.v1.json counterPressure.kinds (drawback | conditional | narrow)"),
        };

        var cp = new UniqueCounterPressureSeed(
            kind, OptStr(cpEl, "severityBand"), OptStr(cpEl, "condition"),
            OptStr(cpEl, "family"), OptStr(cpEl, "channel"));

        var acquisitionId = Str(e, "acquisition");
        var acquisition = acquisitionId switch
        {
            "drop" => UniqueAcquisition.Drop,
            "source-locked" => UniqueAcquisition.SourceLocked,
            "deterministic" => UniqueAcquisition.Deterministic,
            _ => throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed,
                $"unique '{seedId}' declares acquisition '{acquisitionId}', which is not one of " +
                "ssot-uniques.md §4.5's three channels"),
        };

        return new UniqueSeed(
            seedId, containerId, Str(e, "nameKey"), Str(e, "name"), frame, Str(e, "baseType"),
            Str(e, "rarity"), Str(e, "powerAxis"), fixedAtoms, variance, cp, acquisition,
            // The KEY and the authored SENTENCE, together. Reading only the key was the whole of the
            // item-lore wiring gap: 112 real sentences existed and no C# ever opened the field.
            OptStr(e, "flavorKey"), OptStr(e, "flavor"),
            partition, band, OptBool(e, "enabled", @default: true));
    }

    static string Str(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
            throw new UniqueCorpusRejection(UniqueRules.CorpusMalformed, $"missing or non-string '{key}'");
        return el.GetString()!;
    }

    static string? OptStr(JsonElement parent, string key) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    static bool OptBool(JsonElement parent, string key, bool @default) =>
        parent.TryGetProperty(key, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? el.GetBoolean()
            : @default;
}
