using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>Named content rules this module raises, under its own registered namespace — the same
/// "one code with a namespaced payload" shape `EventRules`/`QuestRules` already establish.</summary>
public static class DomainRules
{
    public const string Namespace = "domain";

    public const string DuplicateId = "domain.duplicate";
    public const string BadDangerBand = "domain.bad-danger-band";
    public const string BadEntry = "domain.bad-entry";
    public const string BadClimate = "domain.bad-climate";

    static DomainRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor above to have run — same empty-body idiom
    /// `EventRules.EnsureRegistered`/`QuestRules.EnsureRegistered` already use.</summary>
    public static void EnsureRegistered() { }

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>The load result — a catalog plus every rejection, never a thrown exception
/// (`EventCatalogLoad`/`QuestCatalogLoad`'s own shape): N bad rows report N rejections in one pass,
/// and the catalog holds every GOOD row regardless.</summary>
public readonly record struct DomainCatalogLoad(DomainCatalog Catalog, IReadOnlyList<AtomRejection> Rejections);

/// <summary>
/// D4.15 (spec-domain-catalog.md §1) — validates a domain anchor's `dangerBand` (resolved to an int
/// ordinal AT LOAD, never re-resolved on each later read — the same "compile once" posture
/// `EventCatalog` already takes for its own eligibility trees) and `entry` (closed `once`/`many`,
/// "PLANNED" per the seed contract). <paramref name="dangerBandOrdinals"/> is a plain caller-supplied
/// lookup — the real mapping from a `dangerBand` member id to a difficulty-rung ordinal is
/// `delve-graph-roll`'s own tuning (spec, verbatim: "`shallow` resolves to band 2
/// (`spec-delve-graph-roll.md` §Tunables)"), never something this module re-derives.
/// </summary>
public sealed class DomainCatalog
{
    public static readonly IReadOnlyList<string> EntryValues = new[] { "once", "many" };

    readonly IReadOnlyDictionary<string, DomainRow> _byId;
    readonly IReadOnlyDictionary<string, int> _dangerBandOrdinalById;

    DomainCatalog(IReadOnlyDictionary<string, DomainRow> byId, IReadOnlyDictionary<string, int> dangerBandOrdinalById)
    {
        _byId = byId;
        _dangerBandOrdinalById = dangerBandOrdinalById;
    }

    public int Count => _byId.Count;

    public IReadOnlyList<DomainRow> All => _byId.Values.OrderBy(d => d.DomainId, StringComparer.Ordinal).ToList();

    public DomainRow? Resolve(string domainId) => _byId.TryGetValue(domainId, out var row) ? row : null;

    /// <summary>The resolved int — "ordinals resolve to ints at read" (D4.15's own acceptance line,
    /// verbatim). Every row that reached the catalog resolved successfully by construction, so this
    /// is a plain lookup, never a second validation.</summary>
    public int DangerBandOrdinalFor(string domainId) => _dangerBandOrdinalById[domainId];

    public static DomainCatalogLoad Load(
        IReadOnlyList<DomainRow> rows, IReadOnlyDictionary<string, int> dangerBandOrdinals)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        if (dangerBandOrdinals is null) throw new ArgumentNullException(nameof(dangerBandOrdinals));

        var fails = new List<AtomRejection>();
        var byId = new Dictionary<string, DomainRow>(StringComparer.Ordinal);
        var dangerBandOrdinalById = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows.OrderBy(r => r.DomainId, StringComparer.Ordinal))
        {
            if (byId.ContainsKey(row.DomainId))
            {
                fails.Add(DomainRules.Fail(DomainRules.DuplicateId, $"'{row.DomainId}' is defined twice"));
                continue;
            }

            if (!dangerBandOrdinals.TryGetValue(row.DangerBand, out var ordinal))
            {
                fails.Add(DomainRules.Fail(DomainRules.BadDangerBand, $"{row.DomainId}: '{row.DangerBand}' is not a known dangerBand member"));
                continue;
            }

            if (!EntryValues.Contains(row.Entry, StringComparer.Ordinal))
            {
                fails.Add(DomainRules.Fail(DomainRules.BadEntry, $"{row.DomainId}: entry '{row.Entry}' is not 'once' or 'many'"));
                continue;
            }

            if (!ElementRoster.TryParse(row.Climate, out _))
            {
                fails.Add(DomainRules.Fail(DomainRules.BadClimate, $"{row.DomainId}: '{row.Climate}' is not a known climate"));
                continue;
            }

            byId[row.DomainId] = row;
            dangerBandOrdinalById[row.DomainId] = ordinal;
        }

        return new DomainCatalogLoad(new DomainCatalog(byId, dangerBandOrdinalById), fails);
    }
}
